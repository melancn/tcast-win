namespace TCast.Protocol.Framing;

/// <summary>
/// 字段规则：正文用两个大于号分隔；空字段有意义，必须保留；无统一转义。
/// </summary>
public static class ProtocolFields
{
    /// <summary>分割正文，保留全部空字段（不做 RemoveEmptyEntries）。</summary>
    public static string[] Split(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body.Split(ProtocolLimits.FieldSeparator, StringSplitOptions.None);
    }

    /// <summary>按顺序拼接字段并保留空字段。</summary>
    public static string Join(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return string.Join(ProtocolLimits.FieldSeparator, fields);
    }

    /// <summary>
    /// 校验单个普通字段：不得含 NUL、不得含分隔符、不得为 null。
    /// 已确认的“打包字段”例外由具体命令显式声明，不在此放宽。
    /// </summary>
    /// <exception cref="ProtocolException">字段不合法。</exception>
    public static string Atom(string? value)
    {
        if (value is null)
        {
            throw new ProtocolException("协议字段不得为 null");
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ProtocolException("协议字段不得包含 NUL");
        }

        if (value.Contains(ProtocolLimits.FieldSeparator, StringComparison.Ordinal))
        {
            throw new ProtocolException("协议字段不得包含未转义的分隔符");
        }

        return value;
    }
}
