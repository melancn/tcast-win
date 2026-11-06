using System.Globalization;

namespace TCast.Infrastructure.Apk;

internal static class AxmlManifest
{
    internal readonly record struct Result(string PackageName, string VersionCode, string VersionName, string Label,
        uint LabelResource)
    {
        public uint VersionNameResource { get; init; }
    }

    internal static Result Read(byte[] data, CancellationToken cancellationToken = default)
    {
        ManifestValues values = Collect(data, cancellationToken);
        return new Result(values.PackageName ?? throw new InvalidDataException("清单缺少 package 属性"),
            values.VersionCode, values.VersionName, values.Label, values.LabelResource)
        {
            VersionNameResource = values.VersionNameResource,
        };
    }

    internal static ManifestValues Collect(byte[] data, CancellationToken cancellationToken = default)
    {
        var values = new ManifestValues();
        ApkContainerValidator.ParseBinaryManifest(data, cancellationToken, values);
        return values;
    }

    internal sealed class ManifestValues
    {
        private uint? _versionCode;
        private uint? _versionCodeMajor;

        public string? PackageName { get; private set; }
        public string VersionCode => _versionCode.HasValue || _versionCodeMajor.HasValue
            ? (((ulong)(_versionCodeMajor ?? 0) << 32) | (_versionCode ?? 0)).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        public string VersionName { get; private set; } = string.Empty;
        public string Label { get; private set; } = string.Empty;
        public uint LabelResource { get; private set; }
        public uint VersionNameResource { get; private set; }

        internal void ReadAttribute(string element, string ns, string name, byte type, uint value, string? text)
        {
            if (element == "manifest" && ns.Length == 0 && name == "package")
            {
                if (type != 3 || PackageName is not null)
                {
                    throw new InvalidDataException("清单 package 属性无效或重复");
                }

                PackageName = text;
                return;
            }

            if (ns != ApkContainerValidator.AndroidNamespace)
            {
                return;
            }

            switch (element, name)
            {
                case ("manifest", "versionCode") when type is 0x10 or 0x11:
                    _versionCode = value;
                    break;
                case ("manifest", "versionCodeMajor") when type is 0x10 or 0x11:
                    _versionCodeMajor = value;
                    break;
                case ("manifest", "versionName") when type == 3:
                    VersionName = text ?? string.Empty;
                    break;
                case ("manifest", "versionName") when type == 1:
                    VersionNameResource = value;
                    break;
                case ("application", "label") when type == 3:
                    Label = text ?? string.Empty;
                    break;
                case ("application", "label") when type == 1:
                    LabelResource = value;
                    break;
            }
        }
    }
}
