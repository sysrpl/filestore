using filestore.Models;

namespace filestore.Services;

/// <summary>
/// The in-memory list of AWS profiles and which one is active.
/// Every change is saved to disk straight away and raises <see cref="Changed"/>.
/// </summary>
public sealed class ProfileService
{
    private readonly CredentialStore _store;
    private ProfileData _data = new();

    public ProfileService(CredentialStore store)
    {
        _store = store;
    }

    /// <summary>Raised after profiles are added, edited, deleted, or the active profile changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Profile> Profiles => _data.Profiles;

    public Profile? ActiveProfile => _data.Profiles.FirstOrDefault(p => p.Id == _data.ActiveProfileId);

    public void Load()
    {
        _data = _store.Load();
    }

    public bool IsNameTaken(string name, Guid exceptId) =>
        _data.Profiles.Any(p => p.Id != exceptId
            && string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the profile, or replaces the existing one with the same Id.</summary>
    public void Save(Profile profile)
    {
        var index = _data.Profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
            _data.Profiles[index] = profile;
        else
            _data.Profiles.Add(profile);

        // The first profile created becomes the active one.
        _data.ActiveProfileId ??= profile.Id;
        Commit();
    }

    public void Delete(Guid id)
    {
        _data.Profiles.RemoveAll(p => p.Id == id);
        if (_data.ActiveProfileId == id)
            _data.ActiveProfileId = _data.Profiles.FirstOrDefault()?.Id;
        Commit();
    }

    public void SetActive(Guid id)
    {
        if (_data.ActiveProfileId == id || _data.Profiles.All(p => p.Id != id))
            return;

        _data.ActiveProfileId = id;
        Commit();
    }

    private void Commit()
    {
        _store.Save(_data);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
