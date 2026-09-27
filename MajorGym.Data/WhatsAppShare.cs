using System.Diagnostics;
using System.Text;
using MajorGym.Data.Entities;

namespace MajorGym.Data;

/// <summary>
/// Windows port of Android's <c>WhatsAppShare</c> object. The message text this builds is
/// byte-for-byte the same copy as the Android golden reference (same lines, same order,
/// same emoji, same omission of the plaintext passkey per Android's own "Fix #3" comment —
/// preserved here unchanged). What differs is purely the platform hand-off: Android fires
/// an <c>Intent</c> at the installed WhatsApp package, falling back to a browser and then a
/// generic share sheet. Windows has no installed-app package to target and no share-sheet
/// equivalent, so this opens the same "https://wa.me/{number}?text=..." deep link with the
/// user's default browser via <see cref="Process.Start"/> — which is exactly the second of
/// Android's own three fallback rungs (browser hands off to WhatsApp Desktop/Web if
/// installed, or to wa.me's own web composer otherwise). The owner still has to press
/// WhatsApp's own Send button; nothing is sent automatically, matching Android's contract.
/// </summary>
public static class WhatsAppShare
{
    /// <summary>Client App download link appended to every new-member WhatsApp receipt —
    /// must stay exactly this URL (Android source, preserved verbatim).</summary>
    private const string ClientAppLink = "https://drive.google.com/file/d/17vqZ6Y95e1GzWlVwE8v6aQDtTQmOeXd0/view?usp=sharing";

    public static string WelcomeMessage(Member member, string passkey)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\uD83C\uDFCB\uFE0F MAJOR GYM");
        sb.AppendLine("\uD83C\uDF89 Welcome! Your membership is now active.");
        sb.AppendLine();
        sb.AppendLine("\uD83D\uDCCB Membership Details");
        sb.AppendLine($"Plan: {member.Plan}");
        sb.AppendLine($"Start: {DateUtils.FormatDate(member.JoinedMillis)}");
        sb.AppendLine($"Expiry: {DateUtils.FormatDate(member.ExpiryMillis)}");
        sb.AppendLine($"Phone: {member.Phone}");
        sb.AppendLine();
        sb.AppendLine("\uD83D\uDCF1 Client App");
        sb.AppendLine("Download the Major Gym Client App:");
        sb.AppendLine($"\uD83D\uDC49 {ClientAppLink}");
        return sb.ToString();
    }

    public static string RenewalMessage(Member member)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hi {member.Name}, your Major Gym membership has been renewed \u2705");
        sb.AppendLine();
        sb.AppendLine($"Plan: {member.Plan}");
        sb.AppendLine($"Due Amount: {DateUtils.FormatMoney(member.Fee)}");
        sb.AppendLine($"New Expiry Date: {DateUtils.FormatDate(member.ExpiryMillis)}");
        var days = DateUtils.DaysBetweenNow(member.ExpiryMillis);
        if (days >= 0) sb.AppendLine($"Days Remaining: {days}");
        sb.AppendLine();
        sb.AppendLine("Thanks for staying with us \u2014 see you at the gym!");
        return sb.ToString();
    }

    /// <summary>Normalizes a stored 10-digit phone into WhatsApp's expected
    /// country-code-prefixed form. Ported verbatim from Android's private
    /// <c>whatsAppNumber</c>.</summary>
    private static string WhatsAppNumber(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 10) return "91" + digits;
        if (digits.Length == 12 && digits.StartsWith("91")) return digits;
        return digits;
    }

    /// <summary>Opens the default browser on the wa.me deep link with <paramref name="message"/>
    /// pre-filled. If no browser can be launched (e.g. no shell association in this
    /// environment), the failure is swallowed and reported back via the bool return so the
    /// caller can fall back to "copy to clipboard" instead of crashing the screen — there is
    /// no Windows equivalent of Android's generic Intent share-sheet fallback to drop to.</summary>
    public static bool ShareText(string phone, string message)
    {
        var url = $"https://wa.me/{WhatsAppNumber(phone)}?text={Uri.EscapeDataString(message)}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Share(Member member, string passkey) => ShareText(member.Phone, WelcomeMessage(member, passkey));

    public static bool ShareRenewal(Member member) => ShareText(member.Phone, RenewalMessage(member));
}
