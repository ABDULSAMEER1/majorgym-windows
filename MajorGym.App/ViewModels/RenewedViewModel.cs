using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>RenewalSuccessScreen</c> composable (ui/Screens.kt ~1650-1753).
/// Reached two ways, matching Android exactly: with JustRenewed = true right after
/// <see cref="RenewViewModel"/>'s Confirm Renewal (heading "MEMBERSHIP RENEWED", a "Share
/// Renewal Update" action), or JustRenewed = false as a bare QR-regeneration confirmation
/// (heading "QR UPDATED", no share action) — the latter route exists in Android's Screen
/// sealed class and this port's <see cref="Screen.Renewed"/> record for a future "Regenerate
/// QR" action on Profile that Stage 4 does not yet add a button for; the ViewModel/View
/// themselves handle both cases correctly today.
/// </summary>
public sealed class RenewedViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;
    public Member Member { get; }
    public bool JustRenewed { get; }

    public BitmapImage QrImage { get; }
    public string HeadingText => JustRenewed ? "MEMBERSHIP RENEWED" : "QR UPDATED";
    public bool IsQrValid { get; }
    public string QrStatusText => IsQrValid
        ? $"Valid until {DateUtils.FormatDateTime(Member.QrTokenExpiryMillis)}"
        : "Expired \u2014 regenerate before sharing";

    public ICommand ShareRenewalCommand { get; }
    public ICommand DoneCommand { get; }

    private string? _shareStatus;
    public string? ShareStatus { get => _shareStatus; private set { _shareStatus = value; OnPropertyChanged(); } }

    public RenewedViewModel(NavigationViewModel nav, Member member, bool justRenewed)
    {
        _nav = nav;
        Member = member;
        JustRenewed = justRenewed;
        QrImage = BitmapImageUtils.FromGdiBitmap(QrUtils.MemberQrBitmap(member));
        IsQrValid = QrUtils.IsTokenValid(member);

        ShareRenewalCommand = new RelayCommand(() =>
            ShareStatus = WhatsAppShare.ShareRenewal(Member)
                ? null
                : "Couldn't open a browser to share via WhatsApp — no default browser is configured in this environment.");
        DoneCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Profile(Member.Id)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
