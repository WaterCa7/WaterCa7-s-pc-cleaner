using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PcCleaner.Modules.Profile;

public partial class SignInWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _closed;

    public SignInWindow(SignInViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // PasswordBox.Password is not bindable (on purpose): copy each box into the model, nowhere else.
        PasswordBox.PasswordChanged += (_, _) => vm.Password = PasswordBox.Password;
        NewPasswordBox.PasswordChanged += (_, _) => vm.Password = NewPasswordBox.Password;
        NewPasswordBox2.PasswordChanged += (_, _) => vm.Password2 = NewPasswordBox2.Password;
        RegisterPasswordBox.PasswordChanged += (_, _) => vm.Password = RegisterPasswordBox.Password;
        RegisterPasswordBox2.PasswordChanged += (_, _) => vm.Password2 = RegisterPasswordBox2.Password;
        // The boxes of a step that is left must not show a password the model no longer holds (all the boxes feed the same two
        // fields): on arriving at a step with boxes they start empty, the model's fields too. The code step (after the sign-up)
        // has no boxes, so the password of the sign-up stays for its retry.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SignInViewModel.Step) && vm.Step is SignInStep.Password or SignInStep.ResetCode or SignInStep.Register)
                ClearPasswordBoxes();
        };
        vm.Completed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            try
            {
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                Close(); // not shown as a dialog
            }
        });
        _tick.Tick += (_, _) => vm.Tick();
        _tick.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _tick.Stop();
            vm.Stop(); // ends a running browser poll and the wait behind the list of computers
            ClearPasswordBoxes();
        };
    }

    private void ClearPasswordBoxes()
    {
        foreach (var box in new[] { PasswordBox, NewPasswordBox, NewPasswordBox2, RegisterPasswordBox, RegisterPasswordBox2 })
            box.Clear();
    }
}
