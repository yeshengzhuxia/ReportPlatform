using System.Text.RegularExpressions;

namespace ReportPlatform.Services;

public static partial class UsernamePolicy
{
    public const string ErrorMessage = "登录账号需为 2–80 位英文字母、数字或 _ . @ -，不能包含中文或空格；中文姓名请填写在“名称”中。";

    [GeneratedRegex(@"\A[A-Za-z0-9_.@-]{2,80}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? username) => username is not null && Pattern().IsMatch(username);
}
