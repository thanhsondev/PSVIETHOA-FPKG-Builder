using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Một ngôn ngữ PlayGo trong danh sách chọn (chế độ "Tạo theo ngôn ngữ" của engine tích hợp).</summary>
public sealed partial class PlayGoLanguageOption(string code, string name, Action changed) : ObservableObject
{
    public string Code { get; } = code;

    /// <summary>"ja-JP · Japanese".</summary>
    public string Label { get; } = code + " · " + name;

    [ObservableProperty] private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => changed();
}

/// <summary>
/// Bố cục PlayGo của engine tích hợp (0.6.11): giữ bố cục của nguồn (mặc định, như fpkg-gui "Source layout (automatic)") hoặc tạo
/// theo danh sách ngôn ngữ người dùng chọn ("Generate for languages").
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>0 = bố cục của nguồn (tự động), 1 = tạo theo ngôn ngữ.</summary>
    [ObservableProperty] private int _playGoLayoutIndex;

    private bool _bulkPlayGoLanguages;

    private ObservableCollection<PlayGoLanguageOption>? _playGoLanguageOptions;

    /// <summary>31 ngôn ngữ PlayGo của PS5 theo thứ tự id, mặc định chọn hết.</summary>
    public ObservableCollection<PlayGoLanguageOption> PlayGoLanguageOptions => _playGoLanguageOptions ??= new(
        PlayGoLanguageCatalog.Supported.Select(language => new PlayGoLanguageOption(language.Code, language.Name, OnPlayGoLanguageToggled)));

    public bool PreserveSourcePlayGoLayout => PlayGoLayoutIndex == 0;

    /// <summary>Danh sách ngôn ngữ chỉ dùng khi tạo theo ngôn ngữ với engine tích hợp.</summary>
    public bool PlayGoLanguagesEnabled => EngineOptionsEnabled && !PreserveSourcePlayGoLayout;

    public string PlayGoLanguagesSummary
    {
        get
        {
            var selected = PlayGoLanguageOptions.Count(option => option.IsSelected);
            return selected == PlayGoLanguageOptions.Count ? Loc.T("Advanced.PlayGoLanguagesAll") : Loc.F("Advanced.PlayGoLanguagesSome", selected, PlayGoLanguageOptions.Count);
        }
    }

    /// <summary>Ngôn ngữ cho BuildRequest: null = mọi ngôn ngữ (hoặc giữ bố cục nguồn), danh sách rỗng = chưa chọn gì (báo lỗi khi kiểm tra).</summary>
    private IReadOnlyList<string>? SelectedPlayGoLanguages()
    {
        var selected = PlayGoLanguageOptions.Where(option => option.IsSelected).Select(option => option.Code).ToList();
        return PreserveSourcePlayGoLayout || selected.Count == PlayGoLanguageOptions.Count ? null : selected;
    }

    /// <summary>Đặt lại danh sách từ settings: null = chọn hết.</summary>
    private void ApplyPlayGoLanguages(IReadOnlyCollection<string>? codes)
    {
        SetAllPlayGoLanguages(option => codes == null || codes.Contains(option.Code, StringComparer.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void SelectAllPlayGoLanguages() => SetAllPlayGoLanguages(_ => true);

    [RelayCommand]
    private void SelectNoPlayGoLanguages() => SetAllPlayGoLanguages(_ => false);

    private void SetAllPlayGoLanguages(Func<PlayGoLanguageOption, bool> selected)
    {
        _bulkPlayGoLanguages = true;
        try
        {
            foreach (var option in PlayGoLanguageOptions)
            {
                option.IsSelected = selected(option);
            }
        }
        finally
        {
            _bulkPlayGoLanguages = false;
        }

        OnPlayGoLanguageToggled();
    }

    private void OnPlayGoLanguageToggled()
    {
        if (_bulkPlayGoLanguages)
        {
            return;
        }

        OnPropertyChanged(nameof(PlayGoLanguagesSummary));
        RefreshValidation();
        if (_lastMetadata != null && !PreserveSourcePlayGoLayout)
        {
            PlayGoText = DescribePlayGo(_lastMetadata);
        }
    }

    partial void OnPlayGoLayoutIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PreserveSourcePlayGoLayout));
        OnPropertyChanged(nameof(PlayGoLanguagesEnabled));
        RefreshValidation();
        if (_lastMetadata != null)
        {
            PlayGoText = DescribePlayGo(_lastMetadata);
        }
    }
}
