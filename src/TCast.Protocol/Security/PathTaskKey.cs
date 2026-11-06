using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TCast.Protocol.Security;

/// <summary>
/// 默认任务 key：绝对路径 UTF-8 的小写 MD5（兼容既有固件与 Node 参考实现）。
/// </summary>
/// <remarks>
/// 这是路由与关联 ID，不是文件内容校验，也不是鉴权令牌。
/// 同一路径重试会复用 key，存在旧尝试迟到回包命中新尝试的风险。
/// </remarks>
public static class PathTaskKey
{
    /// <summary>计算 key。</summary>
    public static string FromPath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(absolutePath));
        return Convert.ToHexStringLower(hash);
    }
}
