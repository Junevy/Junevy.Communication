namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 协议层（REST / SOAP）把收到的响应解释为结果的委托（内部）。Http 命名空间不认识 REST 与 SOAP，只调用它；
/// 实现应把解释失败转换为失败结果而不是抛出异常（抛出的异常由尝试循环兜底为协议违规）。
/// </summary>
/// <typeparam name="T">结果的数据类型。</typeparam>
/// <param name="response">收到的响应（正文已经过拦截器处理）。</param>
/// <returns>解释后的结果。</returns>
internal delegate WebApiResult<T> ResponseInterpreter<T>(HttpExchangeResponse response);
