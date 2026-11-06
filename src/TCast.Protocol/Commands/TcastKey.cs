namespace TCast.Protocol.Commands;

/// <summary>
/// TCast 键码（来自 src/key-codes.json）。注意不使用 Android KeyEvent 或红外码。
/// </summary>
/// <remarks>
/// 确认键是 <see cref="Ok"/>=15，不是 <see cref="Enter"/>=68。
/// 频道键与固件实测确认前不在 UI 暴露。
/// </remarks>
public enum TcastKey
{
    Up = 11,
    Down = 12,
    Left = 13,
    Right = 14,
    Ok = 15,
    Back = 16,
    SubMenu = 18,
    MainMenu = 19,
    Power = 20,
    VolumeUp = 21,
    VolumeDown = 22,
    Mute = 23,
    ChannelUp = 27,
    ChannelDown = 28,
    Enter = 68,
}

/// <summary>键码到线上数值的映射。</summary>
public static class TcastKeys
{
    /// <summary>取得线上数值。</summary>
    public static int ToCode(TcastKey key) => (int)key;

    /// <summary>尝试从数值还原键名；未知码返回 false（用于诊断，不用于构造指令）。</summary>
    public static bool TryParse(int code, out TcastKey key)
    {
        if (Enum.IsDefined(typeof(TcastKey), code))
        {
            key = (TcastKey)code;
            return true;
        }

        key = default;
        return false;
    }
}
