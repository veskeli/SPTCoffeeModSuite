using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SPTCoffeeModManager.Models;

public sealed class ModProfile : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private bool _isServerProfile;
    private bool _isActive;
    private int _modCount;
    private int _configCount;
    private bool _isProtected;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
        }
    }

    public bool IsServerProfile
    {
        get => _isServerProfile;
        set
        {
            if (_isServerProfile == value)
            {
                return;
            }

            _isServerProfile = value;
            OnPropertyChanged();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            OnPropertyChanged();
        }
    }

    public int ModCount
    {
        get => _modCount;
        set
        {
            if (_modCount == value)
            {
                return;
            }

            _modCount = value;
            OnPropertyChanged();
        }
    }

    public int ConfigCount
    {
        get => _configCount;
        set
        {
            if (_configCount == value)
            {
                return;
            }

            _configCount = value;
            OnPropertyChanged();
        }
    }

    public bool IsProtected
    {
        get => _isProtected;
        set
        {
            if (_isProtected == value)
            {
                return;
            }

            _isProtected = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string DisplayName => IsActive ? $"{Name} (Active)" : Name;

    public ModProfile Clone()
    {
        return new ModProfile
        {
            Name = Name,
            IsServerProfile = IsServerProfile,
            IsActive = IsActive,
            ModCount = ModCount,
            ConfigCount = ConfigCount,
            IsProtected = IsProtected
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (propertyName == nameof(Name) || propertyName == nameof(IsActive))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

