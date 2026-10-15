using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Interstellar.Loader;

public partial class MainWindow : Window
{
    private bool initialized;
    private string page = "Launcher";
    private bool editingPath;
    private IInputElement? previousFocus;
    private readonly string licenses;
    private readonly FrameworkElement[] pageViews;
    private readonly Dictionary<FrameworkElement, Storyboard> transitions = new();
    private readonly Dictionary<FrameworkElement, TransformGroup> motionTransforms = new();
    private static readonly PropertyPath OpacityPath = new(OpacityProperty);
    private static readonly PropertyPath TranslateYPath = new("(0).(1)[1].(2)", RenderTransformProperty, TransformGroup.ChildrenProperty, TranslateTransform.YProperty);
    private static readonly PropertyPath ScaleXPath = new("(0).(1)[0].(2)", RenderTransformProperty, TransformGroup.ChildrenProperty, ScaleTransform.ScaleXProperty);
    private static readonly PropertyPath ScaleYPath = new("(0).(1)[0].(2)", RenderTransformProperty, TransformGroup.ChildrenProperty, ScaleTransform.ScaleYProperty);
    private static readonly CubicEase EaseOut = CreateEase(EasingMode.EaseOut);
    private static readonly CubicEase EaseIn = CreateEase(EasingMode.EaseIn);
    private static readonly CubicEase EaseInOut = CreateEase(EasingMode.EaseInOut);
    private bool modalClosing;
    private bool reducedMotion;
    private bool closingRequested;
    private bool closeCommitted;
    private bool windowClosed;
    private bool updatingNavigation;

    private bool MotionEnabled => !reducedMotion && SystemParameters.ClientAreaAnimation;

    public MainWindow(bool? reduceMotion = null)
    {
        reducedMotion = reduceMotion ?? !SystemParameters.ClientAreaAnimation;
        var motionSettingChanged = MotionDurationExtension.ReducedMotion != reducedMotion;
        MotionDurationExtension.ReducedMotion = reducedMotion;
        if (motionSettingChanged)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var styles = dictionaries.First(dictionary => dictionary.Source?.OriginalString == "Styles.xaml");
            dictionaries[dictionaries.IndexOf(styles)] = new ResourceDictionary { Source = styles.Source };
        }
        InitializeComponent();
        if (OrbitLogo.Source is { IsFrozen: false, CanFreeze: true } logo) logo.Freeze();
        pageViews = new FrameworkElement[] { LauncherPage, ReleasesPage, AboutPage, SettingsPage, ManagePage };
        licenses = ReadEmbeddedText("Assets/Lucide.LICENSE.txt") + Environment.NewLine + Environment.NewLine + ReadEmbeddedText("Assets/Lucide.0.468.0.LICENSE.txt");
        initialized = true;
        InitializeReleaseLists();
        SelectPage("Launcher");
        AppFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        PrepareWindowEntrance();
        Loaded += (_, _) => BeginWindowEntrance();
        Closed += (_, _) =>
        {
            windowClosed = true;
            StopAllMotion();
        };
    }

    private void PrepareWindowEntrance()
    {
        StopMotion(AppFrame);
        AppFrame.Opacity = MotionEnabled ? 0 : 1;
    }

    private void BeginWindowEntrance()
    {
        if (closingRequested || windowClosed) return;
        Transition(AppFrame, 0, 1, 6, 0, 300, 0.975, 1);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || HandleCloseToTray(e)) return;
        if (DeferCloseUntilOperationSettles(e)) return;
        if (closeCommitted || !MotionEnabled || WindowState == WindowState.Minimized) return;
        e.Cancel = true;
        if (closingRequested) return;
        closingRequested = true;
        AppFrame.IsHitTestVisible = false;
        var opacity = AppFrame.Opacity;
        var transform = AppFrame.RenderTransform as TransformGroup;
        var scale = transform?.Children.OfType<ScaleTransform>().FirstOrDefault()?.ScaleX ?? 1;
        var offset = transform?.Children.OfType<TranslateTransform>().FirstOrDefault()?.Y ?? 0;
        foreach (var element in transitions.Keys.Where(element => element != AppFrame).ToArray())
        {
            var childOpacity = element.Opacity;
            var childTransform = element.RenderTransform.CloneCurrentValue();
            StopMotion(element);
            element.Opacity = childOpacity;
            element.RenderTransform = childTransform;
        }
        Transition(AppFrame, opacity, 0, offset, 4, 180, scale, 0.98, completed: () =>
        {
            closeCommitted = true;
            Close();
        }, easingMode: EasingMode.EaseIn);
    }

    private void StopMotion(FrameworkElement element)
    {
        CancelTransition(element);
        element.Opacity = 1;
        element.RenderTransform = Transform.Identity;
    }

    private void CancelTransition(FrameworkElement element)
    {
        if (transitions.TryGetValue(element, out var storyboard))
        {
            transitions.Remove(element);
            storyboard.Remove(this);
        }
        element.BeginAnimation(OpacityProperty, null);
    }

    private void StopAllMotion()
    {
        foreach (var element in transitions.Keys.ToArray()) StopMotion(element);
        foreach (var element in new FrameworkElement[] { AppFrame, BodyContent, LauncherPage, ReleasesPage, AboutPage, SettingsPage, ManagePage, ReleaseList, UpdateOverlay, UpdateCard, ModalOverlay, ModalCard })
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
        }
    }

    private void Transition(FrameworkElement element, double fromOpacity, double toOpacity, double fromY, double toY, int milliseconds, double fromScale = 1, double toScale = 1, Action? completed = null, EasingMode easingMode = EasingMode.EaseOut)
    {
        CancelTransition(element);
        var animate = MotionEnabled;
        element.Opacity = animate ? fromOpacity : toOpacity;
        if (!motionTransforms.TryGetValue(element, out var transform))
        {
            transform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
            motionTransforms.Add(element, transform);
        }
        var scale = (ScaleTransform)transform.Children[0];
        var translate = (TranslateTransform)transform.Children[1];
        translate.Y = animate ? fromY : toY;
        scale.ScaleX = scale.ScaleY = animate ? fromScale : toScale;
        if (!ReferenceEquals(element.RenderTransform, transform)) element.RenderTransform = transform;
        if (!animate)
        {
            completed?.Invoke();
            return;
        }
        var storyboard = new Storyboard { FillBehavior = FillBehavior.HoldEnd };
        var ease = easingMode == EasingMode.EaseIn ? EaseIn : easingMode == EasingMode.EaseInOut ? EaseInOut : EaseOut;
        void Animate(PropertyPath property, double from, double to)
        {
            if (from == to) return;
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease
            };
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        Animate(OpacityPath, fromOpacity, toOpacity);
        Animate(TranslateYPath, fromY, toY);
        Animate(ScaleXPath, fromScale, toScale);
        Animate(ScaleYPath, fromScale, toScale);
        transitions[element] = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!transitions.TryGetValue(element, out var current) || current != storyboard) return;
            transitions.Remove(element);
            element.Opacity = toOpacity;
            var attached = (TransformGroup)element.RenderTransform;
            ((TranslateTransform)attached.Children[1]).Y = toY;
            var attachedScale = (ScaleTransform)attached.Children[0];
            attachedScale.ScaleX = toScale;
            attachedScale.ScaleY = toScale;
            storyboard.Remove(this);
            completed?.Invoke();
        };
        storyboard.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
    }

    private static CubicEase CreateEase(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }

    private static string ReadEmbeddedText(string name)
    {
        var resource = Application.GetResourceStream(new Uri(name, UriKind.Relative));
        if (resource is null) return "Lucide icons are used under the ISC license. Copyright Lucide Contributors.";
        using var reader = new StreamReader(resource.Stream);
        return reader.ReadToEnd();
    }

    public void SelectPage(string selectedPage)
    {
        if (selectedPage is not ("Launcher" or "Releases" or "About" or "Settings" or "Manage")) throw new ArgumentException("Unknown loader page.", nameof(selectedPage));
        var changed = page != selectedPage;
        var selected = GetPage(selectedPage);
        page = selectedPage;
        if (changed)
        {
            foreach (var element in pageViews) StopMotion(element);
            LauncherPage.Visibility = selectedPage == "Launcher" ? Visibility.Visible : Visibility.Collapsed;
            ReleasesPage.Visibility = selectedPage == "Releases" ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = selectedPage == "About" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = selectedPage == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            ManagePage.Visibility = selectedPage == "Manage" ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            StopMotion(selected);
            selected.Visibility = Visibility.Visible;
        }
        updatingNavigation = true;
        try
        {
            if (LauncherNav.IsChecked != (selectedPage == "Launcher" || selectedPage == "Manage")) LauncherNav.IsChecked = selectedPage == "Launcher" || selectedPage == "Manage";
            if (ReleasesNav.IsChecked != (selectedPage == "Releases")) ReleasesNav.IsChecked = selectedPage == "Releases";
            if (AboutNav.IsChecked != (selectedPage == "About")) AboutNav.IsChecked = selectedPage == "About";
            if (SettingsNav.IsChecked != (selectedPage == "Settings")) SettingsNav.IsChecked = selectedPage == "Settings";
        }
        finally { updatingNavigation = false; }
        if (changed)
        {
            Transition(selected, 0, 1, 10, 0, 230);
        }
    }

    private FrameworkElement GetPage(string name) => name switch
    {
        "Launcher" => LauncherPage,
        "Releases" => ReleasesPage,
        "About" => AboutPage,
        "Settings" => SettingsPage,
        _ => ManagePage
    };

    public void ShowMenuDetails()
    {
        ShowModal("Interstellar Menu", "Version 1.0.0", "Close", false);
    }

    private void ShowModal(string title, string description, string action, bool pathEditor)
    {
        modalOperation = null;
        if (ModalOverlay.Visibility != Visibility.Visible) previousFocus = Keyboard.FocusedElement;
        modalClosing = false;
        ModalCard.IsEnabled = true;
        editingPath = pathEditor;
        ModalTitle.Text = title;
        ModalDescription.Text = description;
        ModalActionLabel.Text = action;
        GameDirectoryInput.Visibility = pathEditor ? Visibility.Visible : Visibility.Collapsed;
        if (pathEditor) GameDirectoryInput.Text = GamePathLabel.Text;
        TitleBar.IsEnabled = false;
        BodyContent.IsEnabled = false;
        ModalOverlay.Visibility = Visibility.Visible;
        Transition(ModalOverlay, 0, 1, 0, 0, 160);
        Transition(ModalCard, 0, 1, 10, 0, 210, 0.975, 1);
        if (pathEditor)
        {
            GameDirectoryInput.Focus();
            GameDirectoryInput.SelectAll();
        }
        else ModalAction.Focus();
    }

    private void DismissModal()
    {
        if (modalClosing || ModalOverlay.Visibility != Visibility.Visible) return;
        editingPath = false;
        modalOperation = null;
        modalClosing = true;
        ModalCard.IsEnabled = false;
        Transition(ModalCard, ModalCard.Opacity, 0, 0, 6, 120, 1, 0.985);
        Transition(ModalOverlay, ModalOverlay.Opacity, 0, 0, 0, 130, completed: () =>
        {
            StopMotion(ModalCard);
            StopMotion(ModalOverlay);
            ModalOverlay.Visibility = Visibility.Collapsed;
            TitleBar.IsEnabled = true;
            BodyContent.IsEnabled = true;
            modalClosing = false;
            if (previousFocus is not null) Keyboard.Focus(previousFocus);
            previousFocus = null;
        });
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (initialized && !updatingNavigation && sender is RadioButton navigation) SelectPage((string)navigation.Tag);
    }

    private void LauncherNav_Click(object sender, RoutedEventArgs e)
    {
        if (page == "Manage") SelectPage("Launcher");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed || e.ClickCount != 1) return;
        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) { ShowMenuDetails(); return; }
        await PerformPrimaryActionAsync();
    }
    private void ReleaseShortcut_Click(object sender, RoutedEventArgs e) => SelectPage("Releases");
    private void ReturnToLauncher_Click(object sender, RoutedEventArgs e) => SelectPage("Launcher");
    private void DismissModal_Click(object sender, RoutedEventArgs e) => DismissModal();
    private void EditPath_Click(object sender, RoutedEventArgs e)
    {
        ShowModal("Game directory", "Enter your Gorilla Tag game directory.", "Save", true);
    }

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        ShowModal("Icon attribution", "Interface icons by Lucide.\n\n" + licenses, "Done", false);
    }

    private async void ModalAction_Click(object sender, RoutedEventArgs e)
    {
        if (modalOperation != null)
        {
            var operation = modalOperation;
            modalOperation = null;
            DismissModal();
            await ExecuteOperationAsync(operation.Label, operation.Action);
            return;
        }
        if (editingPath)
        {
            var path = GameDirectoryInput.Text.Trim();
            if (path.Length == 0)
            {
                ModalDescription.Text = "Enter a game directory, or close this dialog to keep the current path.";
                GameDirectoryInput.Focus();
                return;
            }
            if (productionMode && !GameService.ValidateGameDirectory(path))
            {
                ModalDescription.Text = "Select the Gorilla Tag folder containing the game executable and its data folder.";
                GameDirectoryInput.Focus();
                return;
            }
            GamePathLabel.Text = path;
            GamePathLabel.ToolTip = path;
            SettingsGamePath.Text = path;
            if (productionMode)
            {
                preferences.GameDirectory = System.IO.Path.GetFullPath(path);
                SavePreferences();
            }
        }
        DismissModal();
        if (productionMode) await RefreshStatusAsync();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (closingRequested)
        {
            e.Handled = true;
            return;
        }
        if (ModalOverlay.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Escape)
        {
            DismissModal();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && editingPath)
        {
            ModalAction_Click(sender, e);
            e.Handled = true;
        }
    }
}
