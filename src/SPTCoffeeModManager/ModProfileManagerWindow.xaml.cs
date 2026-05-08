using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using SPTCoffeeModManager.Models;
using SPTCoffeeModManager.Services;

namespace SPTCoffeeModManager;

public partial class ModProfileManagerWindow : INotifyPropertyChanged
{
    private readonly ModProfileRepository _repository;
    private readonly ModProfileFileService _fileService;
    private readonly Dictionary<ModProfile, string> _originalNames = new();

    private ModProfile? _selectedProfile;

    public ModProfileManagerWindow(
        ModProfileRepository repository,
        ModProfileFileService fileService,
        bool createNewOnOpen)
    {
        _repository = repository;
        _fileService = fileService;

        InitializeComponent();

        Profiles = new ObservableCollection<ModProfile>(_repository.EnsureInitialized().Profiles.Select(p => p.Clone()));
        foreach (var profile in Profiles)
        {
            profile.ModCount = _fileService.CountMods(profile);
            profile.ConfigCount = _fileService.CountConfigs(profile);
            _originalNames[profile] = profile.Name;
        }

        DataContext = this;

        if (Profiles.Count > 0)
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.IsActive) ?? Profiles[0];
        }

        if (createNewOnOpen)
        {
            CreateNewProfile();
        }
    }

    public ObservableCollection<ModProfile> Profiles { get; }

    public ModProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value)
            {
                return;
            }

            if (_selectedProfile != null)
            {
                _selectedProfile.PropertyChanged -= SelectedProfile_PropertyChanged;
            }

            _selectedProfile = value;

            if (_selectedProfile != null)
            {
                _selectedProfile.PropertyChanged += SelectedProfile_PropertyChanged;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProfileSelected));
            OnPropertyChanged(nameof(IsDeleteButtonEnabled));
            OnPropertyChanged(nameof(SelectedProfileModsText));
            OnPropertyChanged(nameof(SelectedProfileConfigsText));
        }
    }

    public bool IsProfileSelected => SelectedProfile != null;

    public bool IsDeleteButtonEnabled => SelectedProfile != null && !SelectedProfile.IsProtected;

    public string SelectedProfileModsText => SelectedProfile == null ? "Mods: -" : $"Mods: {SelectedProfile.ModCount}";

    public string SelectedProfileConfigsText => SelectedProfile == null ? "Configs: -" : $"Configs: {SelectedProfile.ConfigCount}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        CreateNewProfile();
    }

    private void DuplicateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            return;
        }

        DuplicateProfile(SelectedProfile);
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            return;
        }

        if (SelectedProfile.IsProtected)
        {
            MessageBox.Show("The 'Default' profile cannot be deleted.", "Cannot Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Profiles.Count <= 1)
        {
            MessageBox.Show("At least one profile must exist.", "Cannot Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var toDelete = SelectedProfile;

        var result = MessageBox.Show($"Delete profile '{toDelete.Name}'?", "Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (toDelete.IsActive)
        {
            var fallback = Profiles.First(p => !ReferenceEquals(p, toDelete));
            try
            {
                var storeForSwitch = BuildStoreFromUi();
                _fileService.ActivateProfile(storeForSwitch, fallback.Name);
                ApplyStoreStateToUi(storeForSwitch);
                _repository.SaveStore(storeForSwitch);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to switch active profile before delete: {ex.Message}", "Profile Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        _fileService.DeleteProfileStorage(toDelete.Name);
        _originalNames.Remove(toDelete);
        Profiles.Remove(toDelete);
        SelectedProfile = Profiles.FirstOrDefault();
        SaveAll();
    }

    private void ActivateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            return;
        }

        try
        {
            var store = BuildStoreFromUi();
            _fileService.ActivateProfile(store, SelectedProfile.Name);
            ApplyStoreStateToUi(store);
            SaveAll();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to activate profile: {ex.Message}", "Profile Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            return;
        }

        try
        {
            ValidateProfile(SelectedProfile);
            SaveAll();
            MessageBox.Show("Profile changes saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CreateNewProfile()
    {
        var baseName = "New Profile";
        var nextName = baseName;
        var index = 1;

        while (Profiles.Any(p => string.Equals(p.Name, nextName, StringComparison.OrdinalIgnoreCase)))
        {
            index++;
            nextName = $"{baseName} {index}";
        }

        var newProfile = new ModProfile
        {
            Name = nextName,
            IsServerProfile = false,
            IsActive = false,
            ModCount = 0
        };

        Profiles.Add(newProfile);
        _originalNames[newProfile] = newProfile.Name;
        SelectedProfile = newProfile;
        SaveAll();

        ProfileNameTextBox.Focus();
        ProfileNameTextBox.SelectAll();
    }

    private void DuplicateProfile(ModProfile sourceProfile)
    {
        var baseName = $"{sourceProfile.Name} Copy";
        var nextName = baseName;
        var index = 1;

        while (Profiles.Any(p => string.Equals(p.Name, nextName, StringComparison.OrdinalIgnoreCase)))
        {
            index++;
            nextName = $"{baseName} {index}";
        }

        var duplicatedProfile = new ModProfile
        {
            Name = nextName,
            IsServerProfile = sourceProfile.IsServerProfile,
            IsActive = false,
            ModCount = 0,
            IsProtected = false
        };

        try
        {
            _fileService.DuplicateProfileStorage(sourceProfile.Name, duplicatedProfile.Name, sourceProfile.IsActive);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to duplicate profile storage: {ex.Message}", "Duplicate Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Profiles.Add(duplicatedProfile);
        _originalNames[duplicatedProfile] = duplicatedProfile.Name;
        SelectedProfile = duplicatedProfile;
        SaveAll();

        ProfileNameTextBox.Focus();
        ProfileNameTextBox.SelectAll();
    }

    private void SaveAll()
    {
        var renamedProfiles = Profiles
            .Where(p => _originalNames.TryGetValue(p, out var oldName) && !string.Equals(oldName, p.Name, StringComparison.Ordinal))
            .Select(p => new { Profile = p, OldName = _originalNames[p] })
            .ToList();

        var store = BuildStoreFromUi();
        ValidateStore(store);

        foreach (var renamed in renamedProfiles)
        {
            _fileService.RenameProfileStorage(renamed.OldName, renamed.Profile.Name);
        }

        _repository.SaveStore(store);

        foreach (var profile in Profiles)
        {
            profile.ModCount = _fileService.CountMods(profile);
            profile.ConfigCount = _fileService.CountConfigs(profile);
            _originalNames[profile] = profile.Name;
        }

        OnPropertyChanged(nameof(SelectedProfileModsText));
        OnPropertyChanged(nameof(SelectedProfileConfigsText));
    }

    private ModProfileStore BuildStoreFromUi()
    {
        var store = new ModProfileStore
        {
            SchemaVersion = 1,
            Profiles = Profiles.Select(p => p.Clone()).ToList()
        };

        store.ActiveProfileName = store.Profiles.FirstOrDefault(p => p.IsActive)?.Name;
        return store;
    }

    private void ApplyStoreStateToUi(ModProfileStore store)
    {
        foreach (var profile in Profiles)
        {
            var updated = store.Profiles.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            if (updated == null)
            {
                continue;
            }

            profile.IsActive = updated.IsActive;
            profile.ModCount = _fileService.CountMods(profile);
            profile.ConfigCount = _fileService.CountConfigs(profile);
        }

        OnPropertyChanged(nameof(SelectedProfileModsText));
        OnPropertyChanged(nameof(SelectedProfileConfigsText));
    }

    private static void ValidateStore(ModProfileStore store)
    {
        if (store.Profiles.Count == 0)
        {
            throw new InvalidOperationException("At least one profile is required.");
        }

        if (!store.Profiles.Any(p => p.IsActive))
        {
            store.Profiles[0].IsActive = true;
        }

        var duplicate = store.Profiles
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate != null)
        {
            throw new InvalidOperationException($"Duplicate profile name: {duplicate.Key}");
        }

        foreach (var profile in store.Profiles)
        {
            ValidateProfile(profile);
        }
    }

    private static void ValidateProfile(ModProfile profile)
    {
        profile.Name = profile.Name.Trim();
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        if (profile.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("Profile name contains invalid path characters.");
        }
    }

    private void SelectedProfile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModProfile.Name) or nameof(ModProfile.ModCount) or nameof(ModProfile.ConfigCount) or nameof(ModProfile.IsActive))
        {
            OnPropertyChanged(nameof(SelectedProfileModsText));
            OnPropertyChanged(nameof(SelectedProfileConfigsText));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}




