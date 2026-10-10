using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Junevy.Communication.Testing;

/// <summary>
/// 测试用自签名证书的生成器（计划 13.3）：RSA 2048，SAN 为 <c>localhost</c> 与 <c>127.0.0.1</c>，EKU 为 serverAuth + clientAuth，
/// 有效期从昨天起两年。证书不写入任何证书存储；<see cref="TestCertificate.Dispose"/> 释放证书并删除为它生成的持久密钥。
/// net8.0 使用 <see cref="CertificateRequest"/>；net472 没有该类型，改为直接调用 Windows CAPI 的 <c>CertCreateSelfSignCertificate</c>。
/// </summary>
public static class TestCertificates
{
    /// <summary>
    /// 生成自签名测试证书（含私钥）。调用方负责 <see cref="TestCertificate.Dispose"/>。
    /// </summary>
    /// <param name="subject">证书主题，默认 <c>CN=localhost</c>。</param>
    /// <returns>测试证书。</returns>
    public static TestCertificate CreateSelfSigned(string subject = "CN=localhost")
    {
#if NET8_0_OR_GREATER
        return CreateWithCertificateRequest(subject);
#else
        return CreateWithCapi(subject);
#endif
    }

#if NET8_0_OR_GREATER
    // SChannel 不接受临时（仅存在于内存中的）密钥：先导出 PFX，再以 Exportable 重新导入，使私钥进入持久的密钥存储。
    private static TestCertificate CreateWithCertificateRequest(string subject)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 selfSigned = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(2));

        string password = Guid.NewGuid().ToString("N");
        byte[] pfx = selfSigned.Export(X509ContentType.Pfx, password);
        var imported = new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable);
        return new TestCertificate(imported, () => DeletePersistedKey(imported));
    }

    // 删除导入时持久化到用户密钥存储的私钥（尽力而为：密钥已不存在或正在使用时忽略）。
    private static void DeletePersistedKey(X509Certificate2 certificate)
    {
        try
        {
            using RSA? rsa = certificate.GetRSAPrivateKey();
            if (rsa is RSACng cng)
                cng.Key.Delete();
        }
        catch (CryptographicException)
        {
            // 密钥已删除或不可访问：测试证书的清理是尽力而为的。
        }
    }
#else
    private const string ProviderName = "Microsoft Enhanced RSA and AES Cryptographic Provider";
    private const uint ProviderType = 24; // PROV_RSA_AES
    private const uint AtKeyExchange = 1;
    private const uint CryptNewKeySet = 0x00000008;
    private const uint CryptDeleteKeySet = 0x00000010;
    private const uint CryptExportable = 0x00000001;
    private const uint X509AsnEncoding = 0x00000001;
    private const uint CertX500NameStr = 3;
    private const string Sha256WithRsaOid = "1.2.840.113549.1.1.11";

    // net472：经 CAPI 创建密钥容器与自签名证书。密钥在持久的容器中，SChannel 可以直接使用；清理时删除该容器。
    private static TestCertificate CreateWithCapi(string subject)
    {
        string container = "junevy-test-" + Guid.NewGuid().ToString("N");
        var allocations = new List<IntPtr>();
        IntPtr provider = IntPtr.Zero;
        IntPtr key = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        try
        {
            if (!CryptAcquireContextW(out provider, container, ProviderName, ProviderType, CryptNewKeySet))
                throw CapiFailure("CryptAcquireContext", Marshal.GetLastWin32Error());

            if (!CryptGenKey(provider, AtKeyExchange, (2048u << 16) | CryptExportable, out key))
                throw CapiFailure("CryptGenKey", Marshal.GetLastWin32Error());

            byte[] encodedName = EncodeName(subject);
            byte[][] extensionValues =
            {
                EncodeSubjectAltName(),
                EncodeEnhancedKeyUsage(),
                EncodeKeyUsage(),
            };
            string[] extensionOids = { "2.5.29.17", "2.5.29.37", "2.5.29.15" };
            bool[] critical = { false, false, true };

            var name = new CERT_NAME_BLOB { cbData = (uint)encodedName.Length, pbData = CopyToNative(encodedName, allocations) };
            var keyInfo = new CRYPT_KEY_PROV_INFO
            {
                pwszContainerName = Marshal.StringToHGlobalUni(container),
                pwszProvName = Marshal.StringToHGlobalUni(ProviderName),
                dwProvType = ProviderType,
                dwFlags = 0,
                cProvParam = 0,
                rgProvParam = IntPtr.Zero,
                dwKeySpec = AtKeyExchange,
            };
            allocations.Add(keyInfo.pwszContainerName);
            allocations.Add(keyInfo.pwszProvName);

            var algorithm = new CRYPT_ALGORITHM_IDENTIFIER
            {
                pszObjId = Marshal.StringToHGlobalAnsi(Sha256WithRsaOid),
                Parameters = new CRYPT_DATA_BLOB(),
            };
            allocations.Add(algorithm.pszObjId);

            DateTime utcNow = DateTime.UtcNow;
            SYSTEMTIME start = ToSystemTime(utcNow.AddDays(-1));
            SYSTEMTIME end = ToSystemTime(utcNow.AddYears(2));

            CERT_EXTENSION[] extensions = new CERT_EXTENSION[extensionOids.Length];
            for (int i = 0; i < extensions.Length; i++)
            {
                extensions[i] = new CERT_EXTENSION
                {
                    pszObjId = Marshal.StringToHGlobalAnsi(extensionOids[i]),
                    fCritical = critical[i] ? 1 : 0,
                    Value = new CRYPT_DATA_BLOB { cbData = (uint)extensionValues[i].Length, pbData = CopyToNative(extensionValues[i], allocations) },
                };
                allocations.Add(extensions[i].pszObjId);
            }

            int size = Marshal.SizeOf(typeof(CERT_EXTENSION));
            IntPtr extensionArray = Marshal.AllocHGlobal(size * extensions.Length);
            allocations.Add(extensionArray);
            for (int i = 0; i < extensions.Length; i++)
                Marshal.StructureToPtr(extensions[i], IntPtr.Add(extensionArray, i * size), fDeleteOld: false);

            var extensionSet = new CERT_EXTENSIONS { cExtension = (uint)extensions.Length, rgExtension = extensionArray };
            context = CertCreateSelfSignCertificate(provider, ref name, 0, ref keyInfo, ref algorithm, ref start, ref end, ref extensionSet);
            if (context == IntPtr.Zero)
                throw CapiFailure("CertCreateSelfSignCertificate", Marshal.GetLastWin32Error());

            X509Certificate2 certificate;
            try
            {
                certificate = new X509Certificate2(context);
            }
            catch
            {
                DeleteContainer(container);
                throw;
            }

            return new TestCertificate(certificate, () => DeleteContainer(container));
        }
        finally
        {
            if (context != IntPtr.Zero)
                CertFreeCertificateContext(context);
            if (key != IntPtr.Zero)
                CryptDestroyKey(key);
            if (provider != IntPtr.Zero)
                CryptReleaseContext(provider, 0);
            foreach (IntPtr allocation in allocations)
                Marshal.FreeHGlobal(allocation);
        }
    }

    // 删除密钥容器（CRYPT_DELETEKEYSET 不返回提供程序句柄）。
    private static void DeleteContainer(string container)
    {
        CryptAcquireContextW(out IntPtr unused, container, ProviderName, ProviderType, CryptDeleteKeySet);
    }

    private static CryptographicException CapiFailure(string operation, int error)
        => new CryptographicException($"{operation} failed with Win32 error {error}.");

    private static IntPtr CopyToNative(byte[] data, List<IntPtr> allocations)
    {
        IntPtr pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        allocations.Add(pointer);
        return pointer;
    }

    private static SYSTEMTIME ToSystemTime(DateTime utc)
        => new SYSTEMTIME
        {
            wYear = (ushort)utc.Year,
            wMonth = (ushort)utc.Month,
            wDay = (ushort)utc.Day,
            wHour = (ushort)utc.Hour,
            wMinute = (ushort)utc.Minute,
            wSecond = (ushort)utc.Second,
            wMilliseconds = (ushort)utc.Millisecond,
        };

    // 主题名由 CertStrToName 编码（X500 字符串格式）。
    private static byte[] EncodeName(string subject)
    {
        uint size = 0;
        if (!CertStrToNameW(X509AsnEncoding, subject, CertX500NameStr, IntPtr.Zero, null, ref size, IntPtr.Zero))
            throw CapiFailure("CertStrToName", Marshal.GetLastWin32Error());

        byte[] encoded = new byte[size];
        if (!CertStrToNameW(X509AsnEncoding, subject, CertX500NameStr, IntPtr.Zero, encoded, ref size, IntPtr.Zero))
            throw CapiFailure("CertStrToName", Marshal.GetLastWin32Error());

        return encoded;
    }

    // SubjectAltName：dNSName "localhost" 与 iPAddress 127.0.0.1。
    private static byte[] EncodeSubjectAltName()
    {
        byte[] dns = DerEncode(0x82, System.Text.Encoding.ASCII.GetBytes("localhost"));
        byte[] ip = DerEncode(0x87, new byte[] { 127, 0, 0, 1 });
        return DerEncode(0x30, dns, ip);
    }

    // EnhancedKeyUsage：serverAuth 与 clientAuth。
    private static byte[] EncodeEnhancedKeyUsage()
        => DerEncode(0x30, EncodeOid("1.3.6.1.5.5.7.3.1"), EncodeOid("1.3.6.1.5.5.7.3.2"));

    // KeyUsage：digitalSignature 与 keyEncipherment（BIT STRING，5 个未用位）。
    private static byte[] EncodeKeyUsage()
        => DerEncode(0x03, new byte[] { 0x05, 0xA0 });

    private static byte[] EncodeOid(string dotted)
    {
        string[] parts = dotted.Split('.');
        var arcs = new ulong[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            arcs[i] = ulong.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);

        var content = new List<byte> { (byte)(40 * arcs[0] + arcs[1]) };
        for (int i = 2; i < arcs.Length; i++)
            AppendBase128(content, arcs[i]);

        return DerEncode(0x06, content.ToArray());
    }

    private static void AppendBase128(List<byte> content, ulong value)
    {
        var groups = new List<byte> { (byte)(value & 0x7F) };
        value >>= 7;
        while (value > 0)
        {
            groups.Insert(0, (byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        content.AddRange(groups);
    }

    // 只支持短格式与一字节长度的 DER 长度（测试证书的扩展值都很短）。
    private static byte[] DerEncode(byte tag, params byte[][] parts)
    {
        int length = parts.Sum(part => part.Length);
        var bytes = new List<byte> { tag };
        if (length < 0x80)
        {
            bytes.Add((byte)length);
        }
        else
        {
            bytes.Add(0x81);
            bytes.Add((byte)length);
        }

        foreach (byte[] part in parts)
            bytes.AddRange(part);

        return bytes.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_DATA_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CERT_NAME_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_ALGORITHM_IDENTIFIER
    {
        public IntPtr pszObjId;
        public CRYPT_DATA_BLOB Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_KEY_PROV_INFO
    {
        public IntPtr pwszContainerName;
        public IntPtr pwszProvName;
        public uint dwProvType;
        public uint dwFlags;
        public uint cProvParam;
        public IntPtr rgProvParam;
        public uint dwKeySpec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CERT_EXTENSION
    {
        public IntPtr pszObjId;
        public int fCritical;
        public CRYPT_DATA_BLOB Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CERT_EXTENSIONS
    {
        public uint cExtension;
        public IntPtr rgExtension;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptAcquireContextW(out IntPtr phProv, string pszContainer, string pszProvider, uint dwProvType, uint dwFlags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptGenKey(IntPtr hProv, uint algId, uint dwFlags, out IntPtr phKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptDestroyKey(IntPtr hKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptReleaseContext(IntPtr hProv, uint dwFlags);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CertStrToNameW(uint dwCertEncodingType, string pszX500, uint dwStrType, IntPtr pvReserved, byte[]? pbEncoded,
                                              ref uint pcbEncoded, IntPtr ppszError);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertCreateSelfSignCertificate(IntPtr hProv, ref CERT_NAME_BLOB subjectIssuerBlob, uint dwFlags,
                                                               ref CRYPT_KEY_PROV_INFO keyProvInfo, ref CRYPT_ALGORITHM_IDENTIFIER signatureAlgorithm,
                                                               ref SYSTEMTIME startTime, ref SYSTEMTIME endTime, ref CERT_EXTENSIONS extensions);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertFreeCertificateContext(IntPtr pCertContext);
#endif
}

/// <summary>
/// 测试用证书（含私钥）。<see cref="Dispose"/> 释放证书，并删除为它生成的私钥。
/// </summary>
public sealed class TestCertificate : IDisposable
{
    private readonly Action release;
    private bool disposed;

    internal TestCertificate(X509Certificate2 certificate, Action release)
    {
        Certificate = certificate;
        this.release = release;
    }

    /// <summary>证书（含私钥）。</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>指纹（大写十六进制）。</summary>
    public string Thumbprint => Certificate.Thumbprint;

    /// <summary>释放证书并删除私钥。可重复调用。</summary>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Certificate.Dispose();
        release();
    }
}
