namespace Junevy.Communication.Modbus.Core.Models
{
    /// <summary>
    /// Modbus 响应数据类，用于封装 Modbus 响应数据。
    /// 不可变化（sealed + 只读属性）：只能通过 <see cref="Success"/> 与 <see cref="Fail(string, T?)"/> /
    /// <see cref="Fail(string, ModbusErrorKind, T?)"/> 三个工厂方法构造，构造后不可修改。
    /// </summary>
    public sealed class ModbusResult<T>
    {
        private ModbusResult(bool isSuccess, T? data, string? errorMessage, ModbusErrorKind errorKind)
        {
            IsSuccess = isSuccess;
            Data = data;
            ErrorMessage = errorMessage;
            ErrorKind = errorKind;
        }

        /// <summary>
        /// 是否成功响应。
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// 响应数据
        /// </summary>
        public T? Data { get; }

        /// <summary>
        /// 错误信息。
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// 失败的机器可读分类（成功时为 <see cref="ModbusErrorKind.None"/>）。
        /// </summary>
        public ModbusErrorKind ErrorKind { get; }

        /// <summary>
        /// 成功响应。
        /// </summary>
        /// <param name="data">响应数据。</param>
        /// <returns>成功响应对象。</returns>
        public static ModbusResult<T> Success(T data)
            => new ModbusResult<T>(true, data, null, ModbusErrorKind.None);

        /// <summary>
        /// 失败响应（未分类，<see cref="ModbusErrorKind.Unspecified"/>）。
        /// </summary>
        /// <param name="errMsg">错误信息。</param>
        /// <param name="data">响应数据。</param>
        /// <returns>失败响应对象。</returns>
        public static ModbusResult<T> Fail(string errMsg, T? data = default)
            => new ModbusResult<T>(false, data, errMsg, ModbusErrorKind.Unspecified);

        /// <summary>
        /// 失败响应（携带机器可读的错误分类）。
        /// </summary>
        /// <param name="errMsg">错误信息。</param>
        /// <param name="kind">错误分类；不允许为 <see cref="ModbusErrorKind.None"/>（否则与 <see cref="IsSuccess"/> 矛盾）。</param>
        /// <param name="data">响应数据。</param>
        /// <returns>失败响应对象。</returns>
        /// <exception cref="ArgumentException"><paramref name="kind"/> 为 <see cref="ModbusErrorKind.None"/>。</exception>
        public static ModbusResult<T> Fail(string errMsg, ModbusErrorKind kind, T? data = default)
        {
            if (kind == ModbusErrorKind.None)
                throw new ArgumentException("A failed result cannot carry ModbusErrorKind.None.", nameof(kind));

            return new ModbusResult<T>(false, data, errMsg, kind);
        }
    }
}