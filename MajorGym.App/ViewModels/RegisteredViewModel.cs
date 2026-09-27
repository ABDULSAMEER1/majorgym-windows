using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>RegistrationSuccessScreen</c> composable
/// (ui/RegistrationSuccessScreen.kt). Shown exactly once, right after a new member is saved
/// — the member's QR (ID only, no personal data) plus the "Share Welcome Message" /
/// "Enroll Fingerprint Now" / "Done" actions. The staggered fade-in choreography in the
/// Android version is purely decorative (the member is already saved before this screen is
/// ever reached — nothing functional depends on it), so this port renders everything
/// immediately rather than reproducing the four-stage delay sequence; every actual piece of
/// content and every action is preserved.
/// </summary>
public sealed class RegisteredViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;
    public Member Member { get; }
    private readonly string _passkey;

    public BitmapImage QrImage { get; }
    public string MemberIdShortText => $"Member ID: {Member.Id[..Math.Min(8, Member.Id.Length)]}\u2026";
    public string QrValidUntilText => $"QR valid until {DateUtils.FormatDateTime(Member.QrTokenExpiryMillis)}";

    private string? _shareStatus;
    public string? ShareStatus { get => _shareStatus; private set { _shareStatus = value; OnPropertyChanged(); } }

    public ICommand ShareWelcomeCommand { get; }
    public ICommand EnrollFingerprintCommand { get; }
    public ICommand DoneCommand { get; }

    public RegisteredViewModel(NavigationViewModel nav, Member member, string passkey)
    {
        _nav = nav;
        Member = member;
        _passkey = passkey;
        QrImage = BitmapImageUtils.FromGdiBitmap(QrUtils.MemberQrBitmap(member));

        ShareWelcomeCommand = new RelayCommand(ShareWelcome);
        EnrollFingerprintCommand = new RelayCommand(() =>
            _nav.NavigateTo(new Screen.EnrollFingerprint(Member.Id, new Screen.Profile(Member.Id))));
        DoneCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Profile(Member.Id)));
    }

    private void ShareWelcome()
    {
        ShareStatus = WhatsAppShare.Share(Member, _passkey)
            ? null
            : "Couldn't open a browser to share via WhatsApp — no default browser is configured in this environment.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
