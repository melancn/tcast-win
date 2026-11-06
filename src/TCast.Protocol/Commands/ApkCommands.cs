using System.Globalization;
using TCast.Protocol.Framing;
using TCast.Protocol.Models;

namespace TCast.Protocol.Commands;

/// <summary>
/// 本地安装命令（223>>INSTALL>>...）与 size 字段算法。
/// </summary>
/// <remarks>
/// 首版只开放本地安装格式：索引 8 为空、索引 9 为任务 key。
/// 商店安装格式（索引 5=1、版本号在第 6 位）字段布局不同，不得混用。
/// </remarks>
public static class ApkCommands
{
    /// <summary>
    /// 把字节数换算成协议 size 字段：按 1024 缩放、保留一位小数、HALF_EVEN 舍入、去掉尾零与单位。
    /// 与 src/apk-server.mjs 的整数算法等价，不用二进制浮点做舍入。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">字节数为负。</exception>
    public static string SizeField(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes == 0)
        {
            return "0";
        }

        int unit = 0;
        long divisor = 1;
        while (unit < 4 && bytes >= divisor * 1024)
        {
            divisor *= 1024;
            unit++;
        }

        // scaled = bytes * 10 / divisor，余数用于 HALF_EVEN 判定（全部整数运算）。
        long scaled = bytes * 10 / divisor;
        long remainder = bytes * 10 % divisor;
        if (remainder * 2 > divisor || (remainder * 2 == divisor && scaled % 2 == 1))
        {
            scaled++;
        }

        long whole = scaled / 10;
        long frac = scaled % 10;
        return frac == 0
            ? whole.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{whole}.{frac}");
    }

    /// <summary>
    /// 构造本地安装正文。
    /// </summary>
    /// <param name="metadata">自动解析结果（包名不允许手填）。</param>
    /// <param name="url">电脑端 HTTP 下载地址，需含实际监听端口。</param>
    /// <param name="bytes">APK 字节数；size 字段由 <see cref="SizeField"/> 换算。</param>
    /// <param name="taskKey">任务关联 key（路由与关联 ID，不是内容校验或鉴权令牌）。</param>
    public static string LocalInstall(ApkMetadata metadata, Uri url, long bytes, string taskKey)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        string[] fields =
        [
            "223",
            "INSTALL",
            ProtocolFields.Atom(metadata.PackageName),
            ProtocolFields.Atom(metadata.DisplayName),
            ProtocolFields.Atom(url.AbsoluteUri),
            "2",
            "0",
            SizeField(bytes),
            string.Empty,
            ProtocolFields.Atom(taskKey),
        ];
        return ProtocolFields.Join(fields);
    }
}
