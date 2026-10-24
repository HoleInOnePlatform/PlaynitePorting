#nullable enable
namespace HoleInOne.SteamLink;

public static class CloudLoginStatus
{
    public static string ForFailure(string? code) => code switch
    {
        // A native startup timeout does not prove that the refresh token expired.
        // Only an explicit authentication rejection can invalidate the stored link.
        "STEAM_LOGON_REJECTED" => "reauth_required",
        "NATIVE_ACCOUNT_MISMATCH" or "STEAM_ACCOUNT_MISMATCH" => "account_mismatch",
        _ => "failed"
    };
}
