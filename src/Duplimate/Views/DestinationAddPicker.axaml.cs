using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Duplimate.Models;

namespace Duplimate.Views;

/// <summary>
/// Shared destination-picker UserControl. Inputs are styled properties
/// so Easy mode (OnboardingWindow) and Advanced mode (BackupEditorWindow)
/// can both bind their own VM properties against this single component.
///
/// The "Add" button's IsEnabled state is computed INTERNALLY from the
/// nullness of <see cref="SelectedDestination"/> — that's the
/// consistency guarantee the user asked for: any consumer of this
/// control gets the same disabled-when-nothing-picked behaviour
/// without having to wire it up themselves.
/// </summary>
public partial class DestinationAddPicker : UserControl
{
    public static readonly StyledProperty<IEnumerable?> AvailableDestinationsProperty =
        AvaloniaProperty.Register<DestinationAddPicker, IEnumerable?>(
            nameof(AvailableDestinations));

    /// <summary>The list of destinations the user can choose from in
    /// the dropdown. Typically a filtered IEnumerable&lt;Destination&gt;
    /// (already-added targets removed by the host VM).</summary>
    public IEnumerable? AvailableDestinations
    {
        get => GetValue(AvailableDestinationsProperty);
        set => SetValue(AvailableDestinationsProperty, value);
    }

    public static readonly StyledProperty<Destination?> SelectedDestinationProperty =
        AvaloniaProperty.Register<DestinationAddPicker, Destination?>(
            nameof(SelectedDestination), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The currently picked destination in the dropdown. Two-
    /// way bound so a host VM that nulls it out (e.g. after
    /// AddCommand fires) clears the dropdown.</summary>
    public Destination? SelectedDestination
    {
        get => GetValue(SelectedDestinationProperty);
        set => SetValue(SelectedDestinationProperty, value);
    }

    public static readonly StyledProperty<bool> HasMoreToAddProperty =
        AvaloniaProperty.Register<DestinationAddPicker, bool>(nameof(HasMoreToAdd));

    /// <summary>True iff there's at least one destination still
    /// available to add. Drives the picker-row visibility — when no
    /// candidates remain, the dropdown hides and only the
    /// "create-new" link stays so the user has a forward path.</summary>
    public bool HasMoreToAdd
    {
        get => GetValue(HasMoreToAddProperty);
        set => SetValue(HasMoreToAddProperty, value);
    }

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<DestinationAddPicker, ICommand?>(nameof(AddCommand));

    /// <summary>Fires when the user clicks "Add". Host VM is expected
    /// to take <see cref="SelectedDestination"/> and append it to the
    /// committed targets, then null SelectedDestination so the
    /// dropdown returns to its placeholder.</summary>
    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> CreateNewCommandProperty =
        AvaloniaProperty.Register<DestinationAddPicker, ICommand?>(nameof(CreateNewCommand));

    /// <summary>Fires when the user clicks the "+ create new"
    /// link below the picker — opens the Destination editor modally.</summary>
    public ICommand? CreateNewCommand
    {
        get => GetValue(CreateNewCommandProperty);
        set => SetValue(CreateNewCommandProperty, value);
    }

    public static readonly StyledProperty<string> CreateNewLabelProperty =
        AvaloniaProperty.Register<DestinationAddPicker, string>(
            nameof(CreateNewLabel), defaultValue: "Or set up a new destination…");

    /// <summary>Label for the create-new link. Host VMs flex this
    /// between "Or set up a new destination…" (no destinations
    /// added yet) and "Add another destination" (one or more
    /// already added) so the copy reflects the user's mental
    /// state.</summary>
    public string CreateNewLabel
    {
        get => GetValue(CreateNewLabelProperty);
        set => SetValue(CreateNewLabelProperty, value);
    }

    /// <summary>Internal computed property mirroring "is something
    /// picked in the dropdown?". Drives the Add button's IsEnabled
    /// gate so consumers don't have to remember to wire one up — the
    /// previous Advanced-mode bug (Add stayed enabled with nothing
    /// picked) is impossible by construction now.</summary>
    public static readonly StyledProperty<bool> HasSelectionProperty =
        AvaloniaProperty.Register<DestinationAddPicker, bool>(nameof(HasSelection));

    public bool HasSelection
    {
        get => GetValue(HasSelectionProperty);
        private set => SetValue(HasSelectionProperty, value);
    }

    public DestinationAddPicker()
    {
        InitializeComponent();
        // Initial state: HasSelection mirrors current null-ness.
        // OnPropertyChanged below picks up every subsequent change.
        HasSelection = SelectedDestination is not null;
    }

    /// <summary>
    /// Keep HasSelection in lock-step with SelectedDestination so the
    /// internal IsEnabled binding on the Add button updates without
    /// the call site needing to wire up a change handler.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedDestinationProperty)
            HasSelection = change.NewValue is Destination;
    }
}
