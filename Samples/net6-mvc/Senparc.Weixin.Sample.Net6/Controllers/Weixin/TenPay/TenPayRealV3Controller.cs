﻿/*----------------------------------------------------------------
    Copyright (C) 2021 Senparc
    
    文件名：TenPayRealV3Controller.cs
    文件功能描述：微信支付V3 Controller
    
    
    创建标识：Senparc - 20210820

    修改标识：Senparc - 20210821
    修改描述：加入账单查询/关闭账单Demo

----------------------------------------------------------------*/

/* 注意：TenPayRealV3Controller 为真正微信支付 API V3 的示例 */

//DPBMARK_FILE TenPay
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Senparc.CO2NET.Extensions;
using Senparc.CO2NET.Utilities;
using Senparc.Weixin.Entities;
using Senparc.Weixin.Exceptions;
using Senparc.Weixin.Helpers;
using Senparc.Weixin.MP;
using Senparc.Weixin.MP.AdvancedAPIs;
using Senparc.Weixin.MP.Sample.CommonService.TemplateMessage;
using Senparc.Weixin.Sample.NetCore3.Controllers;
using Senparc.Weixin.Sample.NetCore3.Filters;
using Senparc.Weixin.Sample.NetCore3.Models;
using Senparc.Weixin.TenPayV3;
using Senparc.Weixin.TenPayV3.Apis;
using Senparc.Weixin.TenPayV3.Apis.BasePay;
using Senparc.Weixin.TenPayV3.Apis.BasePay.Entities;
using Senparc.Weixin.TenPayV3.Apis.Entities;
using Senparc.Weixin.TenPayV3.Entities;
using Senparc.Weixin.TenPayV3.Helpers;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace Senparc.Weixin.Sample.Net6.Controllers
{
    public class TenPayRealV3Controller : BaseController
    {
        private static TenPayV3Info _tenPayV3Info;

        public static TenPayV3Info TenPayV3Info
        {
            get
            {
                if (_tenPayV3Info == null)
                {
                    var key = TenPayHelper.GetRegisterKey(Config.SenparcWeixinSetting);

                    _tenPayV3Info =
                        TenPayV3InfoCollection.Data[key];
                }
                return _tenPayV3Info;
            }
        }

        /// <summary>
        /// 用于初始化BasePayApis
        /// </summary>
        private readonly ISenparcWeixinSettingForTenpayV3 _tenpayV3Setting;

        private readonly BasePayApis _basePayApis;

        /// <summary>
        /// trade_no 和 transaction_id 对照表
        /// TODO：可以放入缓存，设置有效时间
        /// </summary>
        public static ConcurrentDictionary<string, string> TradeNumberToTransactionId = new ConcurrentDictionary<string, string>();


        #region 公共方法

        /// <summary>
        /// 生成二维码
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        private static MemoryStream GerQrCodeStream(string url)
        {
            BitMatrix bitMatrix = new MultiFormatWriter().encode(url, BarcodeFormat.QR_CODE, 300, 300);
            var bw = new ZXing.BarcodeWriterPixelData();

            var pixelData = bw.Write(bitMatrix);
            var bitmap = new System.Drawing.Bitmap(pixelData.Width, pixelData.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var fileStream = new MemoryStream();
            var bitmapData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, pixelData.Width, pixelData.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                // we assume that the row stride of the bitmap is aligned to 4 byte multiplied by the width of the image   
                System.Runtime.InteropServices.Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            fileStream.Flush();//.net core 必须要加
            fileStream.Position = 0;//.net core 必须要加

            bitmap.Save(fileStream, System.Drawing.Imaging.ImageFormat.Png);

            fileStream.Seek(0, SeekOrigin.Begin);
            return fileStream;
        }

        /// <summary>
        /// 获取文字图片信息
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static MemoryStream GetTextImageStream(string text)
        {
            MemoryStream fileStream = new MemoryStream();
            var fontSize = 14;
            var wordLength = 0;
            for (int i = 0; i < text.Length; i++)
            {
                byte[] bytes = Encoding.Default.GetBytes(text.Substring(i,1));
                wordLength += bytes.Length > 1 ? 2 : 1;
            }
            using (var bitmap = new System.Drawing.Bitmap(wordLength * fontSize + 20, 14 + 40, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.ResetTransform();//重置图像
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.DrawString(text, new Font("微软雅黑", fontSize, FontStyle.Bold), Brushes.White, 10, 10);
                    bitmap.Save(fileStream, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            fileStream.Seek(0, SeekOrigin.Begin);
            return fileStream;
        }

        public IActionResult Test()
        {
            var fileStream = GetTextImageStream("Native Pay 未能通过签名验证，无法显示二维码");
            return File(fileStream, "image/png");
        }


        #endregion

        public TenPayRealV3Controller()
        {
            _tenpayV3Setting = Senparc.Weixin.Config.SenparcWeixinSetting.TenpayV3Setting;
            _basePayApis = new BasePayApis(_tenpayV3Setting);
        }

        /// <summary>
        /// 获取用户的OpenId
        /// </summary>
        /// <returns></returns>
        public IActionResult Index(int productId = 0, int hc = 0)
        {
            if (productId == 0 && hc == 0)
            {
                return RedirectToAction("ProductList");
            }

            var returnUrl = string.Format("https://sdk.weixin.senparc.com/TenPayRealV3/JsApi");
            var state = string.Format("{0}|{1}", productId, hc);
            var url = OAuthApi.GetAuthorizeUrl(TenPayV3Info.AppId, returnUrl, state, OAuthScope.snsapi_userinfo);

            return Redirect(url);
        }


        #region 产品展示
        /// <summary>
        /// 商品列表
        /// </summary>
        /// <returns></returns>
        public IActionResult ProductList()
        {
            var products = ProductModel.GetFakeProductList();
            return View(products);
        }
        /// <summary>
        /// 商品详情
        /// </summary>
        /// <param name="productId"></param>
        /// <param name="hc"></param>
        /// <returns></returns>
        public ActionResult ProductItem(int productId, int hc)
        {
            var products = ProductModel.GetFakeProductList();
            var product = products.FirstOrDefault(z => z.Id == productId);
            if (product == null || product.GetHashCode() != hc)
            {
                return Content("商品信息不存在，或非法进入！2003");
            }

            //判断是否正在微信端
            if (Senparc.Weixin.BrowserUtility.BrowserUtility.SideInWeixinBrowser(HttpContext))
            {
                //正在微信端，直接跳转到微信支付页面
                return RedirectToAction("JsApi", new { productId = productId, hc = hc });
            }
            else
            {
                //在PC端打开，提供二维码扫描进行支付
                return View(product);
            }
        }

        /// <summary>
        /// 显示支付二维码（Native 支付，或引导到常规支付）
        /// </summary>
        /// <param name="productId"></param>
        /// <param name="isNativePay">是否使用 Native 支付方式</param>
        /// <returns></returns>
        public async Task<IActionResult> ProductPayCode(int productId, int hc, bool isNativePay = false)
        {
            var products = ProductModel.GetFakeProductList();
            var product = products.FirstOrDefault(z => z.Id == productId);
            if (product == null || product.GetHashCode() != hc)
            {
                return Content("商品信息不存在，或非法进入！2004");
            }

            string url = null;
            MemoryStream fileStream = null;//输出图片的URL
            if (isNativePay)
            {
                //使用 Native 支付，输出二维码并展示
                var price = (int)(product.Price * 100);
                var name = product.Name + " - 微信支付 V3 - Native 支付";
                var sp_billno = string.Format("{0}{1}{2}", TenPayV3Info.MchId/*10位*/, SystemTime.Now.ToString("yyyyMMddHHmmss"),
                             TenPayV3Util.BuildRandomStr(6));

                TransactionsRequestData requestData = new(TenPayV3Info.AppId, TenPayV3Info.MchId, name, sp_billno, new TenpayDateTime(DateTime.Now.AddHours(1)), null, TenPayV3Info.TenPayV3Notify, null, new() { currency = "CNY", total = price }, null, null, null, null);

                BasePayApis basePayApis = new BasePayApis();
                var result = await basePayApis.NativeAsync(requestData);
                //进行安全签名验证
                if (result.VerifySignSuccess == true)
                {
                    fileStream = GerQrCodeStream(result.code_url);
                }
                else
                {
                    fileStream = GetTextImageStream("Native Pay 未能通过签名验证，无法显示二维码");
                }
            }
            else
            {
                //使用 JsApi 方式支付，引导到常规的商品详情页面
                url = $"http://sdk.weixin.senparc.com/TenPayRealV3/JsApi?productId={productId}&hc={product.GetHashCode()}&t={SystemTime.Now.Ticks}";
                fileStream = GerQrCodeStream(url);
            }

            return File(fileStream, "image/png");

        }



        #endregion

        #region OAuth授权
        public ActionResult OAuthCallback(string code, string state, string returnUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(code))
                {
                    return Content("您拒绝了授权！");
                }

                if (!state.Contains("|"))
                {
                    //这里的state其实是会暴露给客户端的，验证能力很弱，这里只是演示一下
                    //实际上可以存任何想传递的数据，比如用户ID
                    return Content("验证失败！请从正规途径进入！1001");
                }

                //通过，用code换取access_token
                var openIdResult = OAuthApi.GetAccessToken(TenPayV3Info.AppId, TenPayV3Info.AppSecret, code);
                if (openIdResult.errcode != ReturnCode.请求成功)
                {
                    return Content("错误：" + openIdResult.errmsg);
                }

                HttpContext.Session.SetString("OpenId", openIdResult.openid);//进行登录

                //也可以使用FormsAuthentication等其他方法记录登录信息，如：
                //FormsAuthentication.SetAuthCookie(openIdResult.openid,false);

                return Redirect(returnUrl);
            }
            catch (Exception ex)
            {
                return Content(ex.ToString());
            }
        }
        #endregion

        #region JsApi支付

        //需要OAuth登录
        [CustomOAuth(null, "/TenpayRealV3/OAuthCallback")]
        public async Task<IActionResult> JsApi(int productId, int hc)
        {
            try
            {
                //获取产品信息
                var products = ProductModel.GetFakeProductList();
                var product = products.FirstOrDefault(z => z.Id == productId);
                if (product == null || product.GetHashCode() != hc)
                {
                    return Content("商品信息不存在，或非法进入！1002");
                }

                //var openId = User.Identity.Name;
                var openId = HttpContext.Session.GetString("OpenId");

                string sp_billno = Request.Query["order_no"];//out_trade_no
                if (string.IsNullOrEmpty(sp_billno))
                {
                    //生成订单10位序列号，此处用时间和随机数生成，商户根据自己调整，保证唯一
                    sp_billno = string.Format("{0}{1}{2}", TenPayV3Info.MchId/*10位*/, SystemTime.Now.ToString("yyyyMMddHHmmss"),
                        TenPayV3Util.BuildRandomStr(6));

                    //注意：以上订单号仅作为演示使用，如果访问量比较大，建议增加订单流水号的去重检查。
                }
                else
                {
                    sp_billno = Request.Query["order_no"];
                }

                var timeStamp = TenPayV3Util.GetTimestamp();
                var nonceStr = TenPayV3Util.GetNoncestr();


                //调用下单接口下单
                var name = product == null ? "test" : product.Name;
                var price = product == null ? 100 : (int)(product.Price * 100);//单位：分

                //var xmlDataInfo = new TenPayV3UnifiedorderRequestData(TenPayV3Info.AppId, TenPayV3Info.MchId, body, sp_billno, price, HttpContext.UserHostAddress()?.ToString(), TenPayV3Info.TenPayV3Notify, TenPay.TenPayV3Type.JSAPI, openId, TenPayV3Info.Key, nonceStr);

                var notifyUrl = TenPayV3Info.TenPayV3Notify.Replace("/TenpayV3/", "/TenpayRealV3/");

                //TODO: JsApiRequestData修改构造函数参数顺序
                TransactionsRequestData jsApiRequestData = new(TenPayV3Info.AppId, TenPayV3Info.MchId, name + " - 微信支付 V3", sp_billno, new TenpayDateTime(DateTime.Now.AddHours(1), false), null, notifyUrl, null, new() { currency = "CNY", total = price }, new(openId), null, null, null);

                //var result = TenPayOldV3.Unifiedorder(xmlDataInfo);//调用统一订单接口
                //JsSdkUiPackage jsPackage = new JsSdkUiPackage(TenPayV3Info.AppId, timeStamp, nonceStr,);
                var result = await _basePayApis.JsApiAsync(jsApiRequestData);
                var package = string.Format("prepay_id={0}", result.prepay_id);

                ViewData["product"] = product;

                ViewData["appId"] = TenPayV3Info.AppId;
                ViewData["timeStamp"] = timeStamp;
                ViewData["nonceStr"] = nonceStr;
                ViewData["package"] = package;
                //ViewData["paySign"] = TenPayOldV3.GetJsPaySign(TenPayV3Info.AppId, timeStamp, nonceStr, package, TenPayV3Info.Key);
                ViewData["paySign"] = TenPaySignHelper.CreatePaySign(timeStamp, nonceStr, package);

                //临时记录订单信息，留给退款申请接口测试使用
                HttpContext.Session.SetString("BillNo", sp_billno);
                HttpContext.Session.SetString("BillFee", price.ToString());

                return View();
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                msg += "<br>" + ex.StackTrace;
                msg += "<br>==Source==<br>" + ex.Source;

                if (ex.InnerException != null)
                {
                    msg += "<br>===InnerException===<br>" + ex.InnerException.Message;
                }
                return Content(msg);
            }
        }


        /// <summary>
        /// JS-SDK支付回调地址（在统一下单接口中设置notify_url）
        /// TODO：使用异步方法
        /// </summary>
        /// <returns></returns>
        public IActionResult PayNotifyUrl()
        {
            try
            {

                //ResponseHandler resHandler = new ResponseHandler(HttpContext);
                var resHandler = new TenPayNotifyHandler(HttpContext);
                var orderReturnJson = resHandler.AesGcmDecryptGetObjectAsync<OrderReturnJson>().GetAwaiter().GetResult();

                Senparc.Weixin.WeixinTrace.SendCustomLog("PayNotifyUrl 接收到消息", orderReturnJson.ToJson(true));

                //string return_code = resHandler.GetParameter("return_code");
                //string return_msg = resHandler.GetParameter("return_msg");
                string trade_state = orderReturnJson.trade_state;

                TradeNumberToTransactionId[orderReturnJson.out_trade_no] = orderReturnJson.transaction_id;


                //orderReturnJson.transaction_id
                string res = null;

                //resHandler.SetKey(TenPayV3Info.Key);
                ////验证请求是否从微信发过来（安全）
                //if (resHandler.IsTenpaySign() && return_code.ToUpper() == "SUCCESS")
                //{
                //    res = "success";//正确的订单处理
                //                    //直到这里，才能认为交易真正成功了，可以进行数据库操作，但是别忘了返回规定格式的消息！
                //}
                //else
                //{
                //    res = "wrong";//错误的订单处理
                //}

                //验证请求是否从微信发过来（安全）
                if (orderReturnJson.VerifySignSuccess == true && trade_state == "SUCCESS")
                {
                    res = "success";//正确的订单处理
                                    //直到这里，才能认为交易真正成功了，可以进行数据库操作，但是别忘了返回规定格式的消息！
                }
                else
                {
                    res = "wrong";//错误的订单处理
                }


                /* 这里可以进行订单处理的逻辑 */

                //发送支付成功的模板消息
                try
                {
                    string appId = Config.SenparcWeixinSetting.TenPayV3_AppId;//与微信公众账号后台的AppId设置保持一致，区分大小写。
                    //string openId = resHandler.GetParameter("openid");
                    string openId = orderReturnJson.payer.openid;
                    //var templateData = new WeixinTemplate_PaySuccess("https://weixin.senparc.com", "购买商品", "状态：" + return_code);
                    var templateData = new WeixinTemplate_PaySuccess("https://weixin.senparc.com", "购买商品", "状态：" + trade_state);

                    Senparc.Weixin.WeixinTrace.SendCustomLog("支付成功模板消息参数", appId + " , " + openId);

                    var result = MP.AdvancedAPIs.TemplateApi.SendTemplateMessage(appId, openId, templateData);
                }
                catch (Exception ex)
                {
                    Senparc.Weixin.WeixinTrace.SendCustomLog("支付成功模板消息", ex.ToString());
                }

                #region 记录日志

                var logDir = ServerUtility.ContentRootMapPath(string.Format("~/App_Data/TenPayNotify/{0}", SystemTime.Now.ToString("yyyyMMdd")));
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                var logPath = Path.Combine(logDir, string.Format("{0}-{1}-{2}.txt", SystemTime.Now.ToString("yyyyMMdd"), SystemTime.Now.ToString("HHmmss"), Guid.NewGuid().ToString("n").Substring(0, 8)));

                using (var fileStream = System.IO.File.OpenWrite(logPath))
                {
                    //var notifyXml = resHandler.ParseXML();
                    var notifyJson = orderReturnJson.ToString();
                    //fileStream.Write(Encoding.Default.GetBytes(res), 0, Encoding.Default.GetByteCount(res));
                    fileStream.Write(Encoding.Default.GetBytes(notifyJson), 0, Encoding.Default.GetByteCount(notifyJson));
                    fileStream.Close();
                }
                #endregion

                //        string xml = string.Format(@"<xml>
                //<return_code><![CDATA[{0}]]></return_code>
                //<return_msg><![CDATA[{1}]]></return_msg>
                //</xml>", return_code, return_msg);

                //return Content(xml, "text/xml");

                //TODO: 此处不知是否还需要签名,文档说我们要对微信回调请求验证签名,这里并未说回个OK是否需要签名
                //如果需要,也不知何种方式处理
                //https://pay.weixin.qq.com/wiki/doc/apiv3/wechatpay/wechatpay3_3.shtml
                return Json(new NotifyReturnData(trade_state, trade_state));
            }
            catch (Exception ex)
            {
                WeixinTrace.WeixinExceptionLog(new WeixinException(ex.Message, ex));
                throw;
            }
        }
        #endregion

        #region H5支付
        /// <summary>
        /// H5支付
        /// </summary>
        /// <param name="productId"></param>
        /// <param name="hc"></param>
        /// <returns></returns>
        public async Task<IActionResult> H5Pay(int productId, int hc)
        {
            try
            {
                //获取产品信息
                var products = ProductModel.GetFakeProductList();
                var product = products.FirstOrDefault(z => z.Id == productId);
                if (product == null || product.GetHashCode() != hc)
                {
                    return Content("商品信息不存在，或非法进入！1002");
                }

                string openId = null;//此时在外部浏览器，无法或得到OpenId

                string sp_billno = Request.Query["order_no"];
                if (string.IsNullOrEmpty(sp_billno))
                {
                    //生成订单10位序列号，此处用时间和随机数生成，商户根据自己调整，保证唯一
                    sp_billno = string.Format("{0}{1}{2}", TenPayV3Info.MchId/*10位*/, SystemTime.Now.ToString("yyyyMMddHHmmss"),
                        TenPayV3Util.BuildRandomStr(6));
                }
                else
                {
                    sp_billno = Request.Query["order_no"];
                }

                var timeStamp = TenPayV3Util.GetTimestamp();
                var nonceStr = TenPayV3Util.GetNoncestr();

                var body = product == null ? "test" : product.Name;
                var price = product == null ? 100 : (int)(product.Price * 100);

                var notifyUrl = TenPayV3Info.TenPayV3Notify.Replace("/TenpayV3/", "/TenpayRealV3/");

                TransactionsRequestData.Scene_Info sence_info = new(HttpContext.UserHostAddress()?.ToString(), null, null, new("Wap", null, null, null, null));

                TransactionsRequestData requestData = new(TenPayV3Info.AppId, TenPayV3Info.MchId, body, sp_billno, new TenpayDateTime(DateTime.Now.AddHours(1), false), null, notifyUrl, null, new() { currency = "CNY", total = price }, new(openId), null, null, sence_info);

                //var ip = Request.Params["REMOTE_ADDR"];
                //var xmlDataInfo = new TenPayV3UnifiedorderRequestData(TenPayV3Info.AppId, TenPayV3Info.MchId, body, sp_billno, price, HttpContext.UserHostAddress()?.ToString(), TenPayV3Info.TenPayV3Notify, TenPay.TenPayV3Type.MWEB/*此处无论传什么，方法内部都会强制变为MWEB*/, openId, TenPayV3Info.Key, nonceStr);

                //SenparcTrace.SendCustomLog("H5Pay接口请求", xmlDataInfo.ToJson());
                WeixinTrace.SendCustomLog("H5Pay接口请求", requestData.ToJson());

                //var result = TenPayOldV3.Html5Order(xmlDataInfo);//调用统一订单接口
                //JsSdkUiPackage jsPackage = new JsSdkUiPackage(TenPayV3Info.AppId, timeStamp, nonceStr,);

                var result = await _basePayApis.H5Async(requestData);

                //SenparcTrace.SendCustomLog("H5Pay接口返回", result.ToJson());
                WeixinTrace.SendCustomLog("H5Pay接口返回", result.ToJson());

                if (!result.VerifySignSuccess == true)
                {
                    return Content("未通过验证，请检查数据有效性！");
                }

                //直接跳转
                return Redirect(result.h5_url);
            }
            catch (Exception ex)
            {
                WeixinTrace.WeixinExceptionLog(new WeixinException(ex.Message, ex));
                throw;
            }
        }

        public ActionResult H5PaySuccess(int productId, int hc)
        {
            try
            {
                //TODO：这里可以校验支付是否真的已经成功

                //获取产品信息
                var products = ProductModel.GetFakeProductList();
                var product = products.FirstOrDefault(z => z.Id == productId);
                if (product == null || product.GetHashCode() != hc)
                {
                    return Content("商品信息不存在，或非法进入！1002");
                }
                ViewData["product"] = product;

                return View();
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                msg += "<br>" + ex.StackTrace;
                msg += "<br>==Source==<br>" + ex.Source;

                if (ex.InnerException != null)
                {
                    msg += "<br>===InnerException===<br>" + ex.InnerException.Message;
                }
                return Content(msg);
            }
        }

        #endregion

        #region 订单及退款

        /// <summary>
        /// 退款申请接口
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> Refund()
        {
            try
            {
                WeixinTrace.SendCustomLog("进入退款流程", "1");

                string nonceStr = TenPayV3Util.GetNoncestr();

                string outTradeNo = HttpContext.Session.GetString("BillNo");
                if (!TradeNumberToTransactionId.TryGetValue(outTradeNo, out string transactionId))
                {
                    return Content("transactionId 不正确，可能是服务器还没有收到微信回调确认通知，退款失败。请稍后刷新再试。");
                }

                WeixinTrace.SendCustomLog("进入退款流程", "2 outTradeNo：" + outTradeNo + ",transactionId：" + transactionId);

                string outRefundNo = "OutRefunNo-" + SystemTime.Now.Ticks;
                int totalFee = int.Parse(HttpContext.Session.GetString("BillFee"));
                int refundFee = totalFee;
                string opUserId = TenPayV3Info.MchId;
                var notifyUrl = "https://sdk.weixin.senparc.com/TenPayRealV3/RefundNotifyUrl";
                //var dataInfo = new TenPayV3RefundRequestData(TenPayV3Info.AppId, TenPayV3Info.MchId, TenPayV3Info.Key,
                //    null, nonceStr, null, outTradeNo, outRefundNo, totalFee, refundFee, opUserId, null, notifyUrl: notifyUrl);
                //TODO:该接口参数二选一传入
                var dataInfo = new RefundRequsetData(transactionId, null, outRefundNo, "Senparc TenPayV3 demo退款测试", notifyUrl, null, new RefundRequsetData.Amount(refundFee, null, refundFee, "CNY"), null);


                //#region 新方法（Senparc.Weixin v6.4.4+）
                //var result = TenPayOldV3.Refund(_serviceProvider, dataInfo);//证书地址、密码，在配置文件中设置，并在注册微信支付信息时自动记录
                //#endregion
                var result = await _basePayApis.RefundAsync(dataInfo);

                WeixinTrace.SendCustomLog("进入退款流程", "3 Result：" + result.ToJson());
                ViewData["Message"] = $"退款结果：{result.status} {result.ResultCode}。您可以刷新当前页面查看最新结果。";
                return View();
                //return Json(result, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                WeixinTrace.WeixinExceptionLog(new WeixinException(ex.Message, ex));

                throw;
            }
        }

        /// <summary>
        /// 退款通知地址
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> RefundNotifyUrl()
        {
            WeixinTrace.SendCustomLog("RefundNotifyUrl被访问", "IP" + HttpContext.UserHostAddress()?.ToString());

            string responseCode = "FAIL";
            string responseMsg = "FAIL";
            try
            {
                //ResponseHandler resHandler = new ResponseHandler(null
                var resHandler = new TenPayNotifyHandler(HttpContext);
                var refundNotifyJson = await resHandler.AesGcmDecryptGetObjectAsync<RefundNotifyJson>();

                //string return_code = resHandler.GetParameter("return_code");
                //string return_msg = resHandler.GetParameter("return_msg");
                string refund_status = refundNotifyJson.refund_status;

                //WeixinTrace.SendCustomLog("跟踪RefundNotifyUrl信息", resHandler.ParseXML());
                WeixinTrace.SendCustomLog("跟踪RefundNotifyUrl信息", refundNotifyJson.ToJson());

                if (refund_status == "SUCCESS")
                {
                    responseCode = "SUCCESS";
                    responseMsg = "OK";

                    //string appId = resHandler.GetParameter("appid");
                    //string mch_id = resHandler.GetParameter("mch_id");
                    //string nonce_str = resHandler.GetParameter("nonce_str");
                    //string req_info = resHandler.GetParameter("req_info");

                    //TODO: 返回的数据中并没有mchid该如何判断?
                    //if (!appId.Equals(Senparc.Weixin.Config.SenparcWeixinSetting.TenPayV3_AppId))
                    //{
                    //    /* 
                    //     * 注意：
                    //     * 这里添加过滤只是因为盛派Demo经常有其他公众号错误地设置了我们的地址，
                    //     * 导致无法正常解密，平常使用不需要过滤！
                    //     */
                    //    SenparcTrace.SendCustomLog("RefundNotifyUrl 的 AppId 不正确",
                    //        $"appId:{appId}\r\nmch_id:{mch_id}\r\nreq_info:{req_info}");
                    //    return Content("faild");
                    //}

                    //var decodeReqInfo = TenPayV3Util.DecodeRefundReqInfo(req_info, TenPayV3Info.Key);
                    //var decodeDoc = XDocument.Parse(decodeReqInfo);

                    //获取接口中需要用到的信息
                    //string transaction_id = decodeDoc.Root.Element("transaction_id").Value;
                    //string out_trade_no = decodeDoc.Root.Element("out_trade_no").Value;
                    //string refund_id = decodeDoc.Root.Element("refund_id").Value;
                    //string out_refund_no = decodeDoc.Root.Element("out_refund_no").Value;
                    //int total_fee = int.Parse(decodeDoc.Root.Element("total_fee").Value);
                    //int? settlement_total_fee = decodeDoc.Root.Element("settlement_total_fee") != null
                    //        ? int.Parse(decodeDoc.Root.Element("settlement_total_fee").Value)
                    //        : null as int?;
                    //int refund_fee = int.Parse(decodeDoc.Root.Element("refund_fee").Value);
                    //int tosettlement_refund_feetal_fee = int.Parse(decodeDoc.Root.Element("settlement_refund_fee").Value);
                    //string refund_status = decodeDoc.Root.Element("refund_status").Value;
                    //string success_time = decodeDoc.Root.Element("success_time").Value;
                    //string refund_recv_accout = decodeDoc.Root.Element("refund_recv_accout").Value;
                    //string refund_account = decodeDoc.Root.Element("refund_account").Value;
                    //string refund_request_source = decodeDoc.Root.Element("refund_request_source").Value;

                    //获取接口中需要用到的信息 例
                    string transaction_id = refundNotifyJson.transaction_id;
                    string out_trade_no = refundNotifyJson.out_trade_no;
                    string refund_id = refundNotifyJson.refund_id;
                    string out_refund_no = refundNotifyJson.out_refund_no;
                    int total_fee = refundNotifyJson.amount.payer_total;
                    int refund_fee = refundNotifyJson.amount.refund;


                    WeixinTrace.SendCustomLog("RefundNotifyUrl被访问", "验证通过");
                }

                //进行后续业务处理
            }
            catch (Exception ex)
            {
                responseMsg = ex.Message;
                WeixinTrace.WeixinExceptionLog(new WeixinException(ex.Message, ex));
            }

            //    string xml = string.Format(@"<xml>
            //<return_code><![CDATA[{0}]]></return_code>
            //<return_msg><![CDATA[{1}]]></return_msg>
            //</xml>", responseCode, responseMsg);
            //    return Content(xml, "text/xml");
            //TODO: 此处不知是否还需要签名,文档说我们要对微信回调请求验证签名,这里并未说回个OK是否需要签名
            //如果需要,也不知何种方式处理
            //https://pay.weixin.qq.com/wiki/doc/apiv3/wechatpay/wechatpay3_3.shtml
            return Json(new NotifyReturnData(responseCode, responseMsg));
        }

        /// <summary>
        /// 订单查询
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> OrderQuery(string out_trade_no = null, string transaction_id = null)
        {
            //out_trade_no transaction_id 两个参数不能都为空
            if (out_trade_no is null && transaction_id is null)
            {
                throw new ArgumentNullException(nameof(out_trade_no) + " or " + nameof(transaction_id));
            }

            OrderReturnJson result = null;

            //选择方式查询订单
            if (out_trade_no is not null)
            {
                result = await _basePayApis.OrderQueryByOutTradeNoAsync(out_trade_no, TenPayV3Info.MchId);
            }
            if (transaction_id is not null)
            {
                result = await _basePayApis.OrderQueryByTransactionIdAsync(transaction_id, TenPayV3Info.MchId);
            }

            return Json(result);
        }

        /// <summary>
        /// 关闭订单接口
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> CloseOrder(string out_trade_no)
        {

            //out_trade_no transaction_id 两个参数不能都为空
            if (out_trade_no is null)
            {
                throw new ArgumentNullException(nameof(out_trade_no));
            }

            ReturnJsonBase result = null;
            result = await _basePayApis.CloseOrderAsync(out_trade_no, TenPayV3Info.MchId);

            return Json(result);
        }
        #endregion

        #region 交易账单

        /// <summary>
        /// 申请交易账单
        /// </summary>
        /// <param name="date">日期，格式如：2021-08-27</param>
        /// <returns></returns>
        public async Task<IActionResult> TradeBill(string date)
        {
            if (!Request.IsLocal())
            {
                return Content("出于安全考虑，此操作限定在本机上操作（实际项目可以添加登录权限限制后远程操作）！");
            }


            var filePath = $"{date}-TradeBill.csv";
            Console.WriteLine("FilePath:" + filePath);
            using (var fs = new FileStream(filePath, FileMode.OpenOrCreate))
            {
                BasePayApis basePayApis = new BasePayApis();

                var result = basePayApis.TradeBillQueryAsync(date, fileStream: fs).GetAwaiter().GetResult();

                fs.Flush();

            }
            return Content("已经下载倒指定目录，文件名：" + filePath);
        }

        /// <summary>
        /// 申请资金账单接口
        /// </summary>
        /// <param name="date">日期，格式如：2021-08-27</param>
        /// <returns></returns>
        public async Task<IActionResult> FundflowBill(string date)
        {
            if (!Request.IsLocal())
            {
                return Content("出于安全考虑，此操作限定在本机上操作（实际项目可以添加登录权限限制后远程操作）！");
            }

            var filePath = $"{date}-FundflowBill.csv";
            Console.WriteLine("FilePath:" + filePath);
            using (var fs = new FileStream(filePath, FileMode.OpenOrCreate))
            {
                BasePayApis basePayApis = new BasePayApis();

                var result = await _basePayApis.FundflowBillQueryAsync(date, fs);

                fs.Flush();
            }
            return Content("已经下载倒指定目录，文件名：" + filePath);
        }

        #endregion
    }
}