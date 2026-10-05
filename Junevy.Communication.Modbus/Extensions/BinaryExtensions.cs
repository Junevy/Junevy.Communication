namespace Junevy.Communication.Modbus.Extensions
{
    /// <summary>
    /// Provides methods for converting between ushort values and byte arrays,
    /// with support for both little-endian and big-endian byte ordering.
    /// </summary>
    public static class BinaryExtensions
    {
        /// <summary>
        /// Converts a ushort value to a 2-byte little-endian array (low byte first).
        /// </summary>
        public static byte[] ToLittleEndian(this ushort value)
        {
            return
            [
                (byte)(value & 0x00FF),
                (byte)((value >> 8) & 0x00FF)
            ];
        }

        /// <summary>
        /// Converts a ushort value to a 2-byte big-endian array (high byte first).
        /// </summary>
        public static byte[] ToBigEndian(this ushort value)
        {
            return
            [
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            ];
        }

        /// <summary>
        /// Converts an array of ushort values to a big-endian byte array.
        /// </summary>
        public static byte[] ToBigEndianByteArray(this ushort[] ushorts)
        {
            if (ushorts == null || ushorts.Length == 0) return [];

            byte[] bytes = new byte[ushorts.Length * 2];

            for (int i = 0; i < ushorts.Length; i++)
            {
                bytes[i * 2] = (byte)(ushorts[i] >> 8);
                bytes[i * 2 + 1] = (byte)(ushorts[i] & 0xFF);
            }

            return bytes;
        }

        /// <summary>
        /// Converts an array of ushort values to a hex string (big-endian).
        /// </summary>
        public static string ToHexString(this ushort[] ushorts)
            => ToHexString(ushorts.ToBigEndianByteArray());

        /// <summary>
        /// Converts a byte array to a hex string with dash separators.
        /// </summary>
        public static string ToHexString(this byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            return BitConverter.ToString(bytes);
        }
    }
}
