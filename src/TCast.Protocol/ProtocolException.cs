namespace TCast.Protocol;

/// <summary>
/// 协议层可预期的错误：非法长度、非法字段、非法发现报文、失配的回包。
/// 连接层收到该异常即视为协议错误并关闭 TCP，不做“尽力恢复”。
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
