using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using MajorGym.Data.Entities;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace MajorGym.Data;

/// <summary>
/// Generates the two QR codes this app hands out. Ported from Android's <c>QrUtils</c>
/// object.
///
/// ══════════════════════════════════════════════════════════════════════════════════
/// COMPATIBILITY CONTRACT — DO NOT CHANGE <see cref="OnboardingPayload"/> WITHOUT
/// EXPLICIT SIGN-OFF (Stage 1 report §H risk #3, Stage 2 brief §20):
/// A separate Flutter "Major Gym Client App" scans the member QR and parses this exact
/// JSON shape into its own local cache. The field names, field order (cosmetic in JSON,
/// but kept identical to the Android source for diffability), field types, and the fact
/// that this is a flat single-level object are an external compatibility contract, not
/// an internal implementation detail. This Windows implementation reproduces it exactly.
/// ══════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class QrUtils
{
    /// <summary>Static attendance QR content. One fixed value for the whole gym — never
    /// regenerated, never rotated, unlike the per-member QR below. Every member/client
    /// scans this exact same code to check in. Value must match the Android app's constant
    /// verbatim, since a member's existing printed/saved attendance QR must keep working.</summary>
    public const string GymAttendanceCode = "MAJOR_GYM_ATTENDANCE_2026";

    /// <summary>Shown inside the member's onboarding payload; the client app displays this
    /// as-is rather than hardcoding its own copy. Single gym for v1.</summary>
    public const string GymName = "MAJOR GYM";

    /// <summary>Default window a freshly generated membership QR stays valid for.</summary>
    public const long TokenValidityMillis = 48L * 60 * 60 * 1000; // 48 hours

    /// <summary>Generates a fresh, unpredictable token to embed in a member's QR.</summary>
    public static string FreshToken() => Guid.NewGuid().ToString();

    /// <summary>Whether <paramref name="member"/>'s current token has not yet expired.</summary>
    public static bool IsTokenValid(Member member, long? nowMillis = null)
    {
        var now = nowMillis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return !string.IsNullOrEmpty(member.QrToken) && now < member.QrTokenExpiryMillis;
    }

    /// <summary>
    /// The member QR's content: a compact JSON object with exactly what the client app
    /// needs to onboard/refresh itself, and nothing more (no other members' data, no full
    /// payment history — just this one record's current-state fields). Field names match
    /// what the Flutter client's QrPayloadParser expects — keep the two in sync if either
    /// side ever changes, and never independently. (Android doc comment, preserved.)
    /// </summary>
    public static string OnboardingPayload(Member member)
    {
        var o = new JsonObject
        {
            ["id"] = member.Id,
            ["name"] = member.Name,
            ["phone"] = member.Phone,
            ["plan"] = member.Plan,
            ["fee"] = member.Fee,
            ["joinedMillis"] = member.JoinedMillis,
            ["expiryMillis"] = member.ExpiryMillis,
            ["passwordHash"] = member.PasswordHash,
            ["token"] = member.QrToken,
            ["tokenExpiryMillis"] = member.QrTokenExpiryMillis,
            ["gymName"] = GymName,
            // Renewal/registration history — lets the client show "Last Payment Date" on
            // its Membership page without a separate data channel.
            ["historyJson"] = member.HistoryJson
        };
        return o.ToJsonString();
    }

    /// <summary>Renders <see cref="OnboardingPayload"/> as a black/white QR bitmap, sized
    /// to match Android's default 512x512. Uses ZXing.Net — the .NET port of the same
    /// ZXing algorithm family Android's com.google.zxing is built on — as the Windows
    /// equivalent QR library (Stage 1 report §6).</summary>
    public static Bitmap MemberQrBitmap(Member member, int sizePx = 512) =>
        RenderQrBitmap(OnboardingPayload(member), sizePx);

    /// <summary>Static gym-wide attendance QR — a single fixed string for the whole gym,
    /// never rotated, unlike the per-member QR above. Encodes only the gym ID/attendance
    /// code, no personal data.</summary>
    public static Bitmap GymQrBitmap(string gymId, int sizePx = 512) =>
        RenderQrBitmap(gymId, sizePx);

    private static Bitmap RenderQrBitmap(string content, int sizePx)
    {
        var writer = new QRCodeWriter();
        BitMatrix matrix = writer.encode(content, BarcodeFormat.QR_CODE, sizePx, sizePx,
            new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = 0
            });

        var bitmap = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppRgb);
        for (var x = 0; x < sizePx; x++)
        {
            for (var y = 0; y < sizePx; y++)
            {
                bitmap.SetPixel(x, y, matrix[x, y] ? Color.Black : Color.White);
            }
        }
        return bitmap;
    }
}
