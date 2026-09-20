using ReportPlatform.Models;

namespace ReportPlatform.Services;

public static class ReportIconRules
{
    private static readonly string[] Prefixes =
    [
        "data:image/png;base64,", "data:image/jpeg;base64,", "data:image/webp;base64,"
    ];

    public static void Validate(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return;
        var prefix = Prefixes.FirstOrDefault(icon.StartsWith);
        if (prefix is null) throw new ApiException("报表图标仅支持 PNG、JPG 或 WebP 图片");
        try
        {
            var bytes = Convert.FromBase64String(icon[prefix.Length..]);
            if (bytes.Length is < 1 or > 256 * 1024) throw new ApiException("报表图标不能超过 256 KB");
            var isPng = prefix.Contains("png") && bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var isJpeg = prefix.Contains("jpeg") && bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF });
            var isWebp = prefix.Contains("webp") && bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
            if (!isPng && !isJpeg && !isWebp) throw new ApiException("报表图标内容与图片格式不匹配，请重新上传");
        }
        catch (FormatException) { throw new ApiException("报表图标数据无效，请重新上传"); }
    }
}
