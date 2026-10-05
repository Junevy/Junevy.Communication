namespace Junevy.Communication.Modbus.Core.Models
{
    /// <summary>
    /// Modbus 响应数据类，用于封装 Modbus 响应数据。
    /// </summary>
    public class ModbusResult<T>
    {
        /// <summary>
        /// 是否成功响应。
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 响应数据
        /// </summary>
        public T? Data { get; set; }

        /// <summary>
        /// 错误信息。
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 失败的机器可读分类（成功时为 <see cref="ModbusErrorKind.None"/>）。
        /// </summary>
        public ModbusErrorKind ErrorKind { get; set; }

        /// <summary>
        /// 成功响应。
        /// </summary>
        /// <param name="data">响应数据。</param>
        /// <returns>成功响应对象。</returns>
        /// <param name="rawData">原始响应数据。</param>
        public static ModbusResult<T> Success(T data)
            => new() { IsSuccess = true, Data = data };

        /// <summary>
        /// 失败响应（未分类，<see cref="ModbusErrorKind.Unspecified"/>）。
        /// </summary>
        /// <param name="errMsg">错误信息。</param>
        /// <param name="data">响应数据。</param>
        /// <returns>失败响应对象。</returns>
        public static ModbusResult<T> Fail(string errMsg, T? data = default)
            => new() { IsSuccess = false, ErrorMessage = errMsg, ErrorKind = ModbusErrorKind.Unspecified, Data = data };

        /// <summary>
        /// 失败响应（携带机器可读的错误分类）。
        /// </summary>
        /// <param name="errMsg">错误信息。</param>
        /// <param name="kind">错误分类。</param>
        /// <param name="data">响应数据。</param>
        /// <returns>失败响应对象。</returns>
        public static ModbusResult<T> Fail(string errMsg, ModbusErrorKind kind, T? data = default)
            => new() { IsSuccess = false, ErrorMessage = errMsg, ErrorKind = kind, Data = data };
    }
}
