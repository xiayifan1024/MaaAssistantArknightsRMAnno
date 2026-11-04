// <copyright file="VersionUpdateDialogViewModel.cs" company="MaaAssistantArknights">
// Part of the MaaWpfGui project, maintained by the MaaAssistantArknights team (Maa Team)
// Copyright (C) 2021-2026 MaaAssistantArknights Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License v3.0 only as published by
// the Free Software Foundation, either version 3 of the License, or
// any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY
// </copyright>

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using MaaWpfGui.Configuration.Factory;
using MaaWpfGui.Constants;
using MaaWpfGui.Helper;
using MaaWpfGui.Models;
using MaaWpfGui.Services;
using MaaWpfGui.ViewModels.UI;
using Semver;
using Serilog;
using Stylet;

namespace MaaWpfGui.ViewModels.Dialogs;

/// <summary>
/// The view model of version update.
/// </summary>
public partial class VersionUpdateDialogViewModel : Screen
{
    private static readonly ILogger _logger = Log.ForContext<VersionUpdateDialogViewModel>();

    private static readonly string _contributorAvatarDir = Path.Combine(PathsHelper.CacheDir, "contributor");

    private const string ContributorAvatarPlaceholderName = "_placeholder.png";

    // 32×32 全透明 PNG；头像未下载时占住 16px 位置，下载完成前后布局零跳动
    private const string PlaceholderAvatarBase64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAGklEQVR4nO3BAQEAAACCIP+vbkhAAQAAAO8GECAAARlDNO4AAAAASUVORK5CYII=";

    private static readonly HashSet<string> _downloadingAvatars = [];

    private static string FormatUpdateInfo(string text)
    {
        // MdXaml.Html 的 DetailsParser 用 bool.TryParse 解析 open 属性值，HTML 无值写法 <details open>
        // 解析出空串导致 TryParse 失败、Expander 恒为折叠；改写成带值形式使其自动展开
        text = MyRegex().Replace(text, "open=\"true\"");
        return AddContributorLink(text);
    }

    private static string AddContributorLink(string text)
    {
        /*
        //        "@ " -> "@ "
        //       "`@`" -> "`@`"
        //   "@MistEO" -> "![avatar](path "MistEO"){…}[@MistEO](https://github.com/MistEO)"
        // "[@MistEO]" -> "[@MistEO]"
        */
        // 头像图片与用户名链接平级不嵌套：MdXaml 的内联匹配正则带 Singleline，
        // 嵌套图片链接在 LF 行尾（GitHub/MirrorChyan 的 release body）下会跨行吞掉后续行的内容
        return Regex.Replace(text, @"([^\[`]|^)@([^\s]+)", m => {
            var user = m.Groups[2].Value;
            var avatar = GetContributorAvatarMarkdown(user);
            return $"{m.Groups[1].Value}{avatar}[@{user}](https://github.com/{user})";
        });
    }

    /// <summary>
    /// 生成贡献者头像的内联图片 Markdown：缓存命中用真头像，否则用透明占位图；
    /// 非 GitHub 用户名形状的捕获（可能带尾随标点）不插图，保持纯链接。
    /// </summary>
    private static string GetContributorAvatarMarkdown(string user)
    {
        if (!Regex.IsMatch(user, @"^[a-zA-Z0-9-]+$") || !EnsurePlaceholderAvatar())
        {
            return string.Empty;
        }

        string avatarPath = Path.Combine(_contributorAvatarDir, user + ".png");
        string effectivePath = File.Exists(avatarPath)
            ? avatarPath
            : Path.Combine(_contributorAvatarDir, ContributorAvatarPlaceholderName);

        // 路径用正斜杠，Markdown 中反斜杠是转义字符；尺寸语法 {width=16px} 由 MdXaml 的 ImageResizeExt 渲染；
        // title（即渲染后的 ToolTip）携带用户名，供头像下载完成后在已渲染文档中定位占位图换源
        return $"![avatar]({effectivePath.Replace('\\', '/')} \"{user}\"){{width=16px height=16px}}";
    }

    private static bool _placeholderAvatarReady;

    private static bool EnsurePlaceholderAvatar()
    {
        if (_placeholderAvatarReady)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(_contributorAvatarDir);
            string path = Path.Combine(_contributorAvatarDir, ContributorAvatarPlaceholderName);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, Convert.FromBase64String(PlaceholderAvatarBase64));
            }

            _placeholderAvatarReady = true;
        }
        catch (Exception e)
        {
            // 写盘失败（目录只读等）后不再重试，头像整体退化为纯链接
            _logger.Warning(e, "Failed to create placeholder contributor avatar");
        }

        return _placeholderAvatarReady;
    }

    /// <summary>
    /// 后台下载 changelog 中缺失的贡献者头像（走 GitHub 用户名头像直链，磁盘缓存命中即跳过，每个用户只下一次）。
    /// 每落盘一个就在已渲染文档中把对应占位图换成真头像（不重建 FlowDocument，折叠/滚动状态不受影响）；
    /// 失败静默，下次展示时重试。
    /// </summary>
    /// <param name="markdown">要提取用户名的 changelog 文本；传 null 时取当前 <see cref="UpdateInfo"/>（设置页更新日志按钮等只展示不写入的场景）。</param>
    /// <returns>Task</returns>
    public async Task DownloadMissingContributorAvatarsAsync(string? markdown = null)
    {
        markdown ??= UpdateInfo;
        var users = Regex.Matches(markdown, @"@([a-zA-Z0-9-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        foreach (var user in users)
        {
            string path = Path.Combine(_contributorAvatarDir, user + ".png");
            if (File.Exists(path))
            {
                continue;
            }

            lock (_downloadingAvatars)
            {
                if (!_downloadingAvatars.Add(user))
                {
                    continue;
                }
            }

            try
            {
                using var response = await Instances.HttpService.GetAsync(new Uri($"https://github.com/{user}.png?size=32")).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    Directory.CreateDirectory(_contributorAvatarDir);
                    string tempPath = path + ".temp";
                    await File.WriteAllBytesAsync(tempPath, content).ConfigureAwait(false);
                    File.Move(tempPath, path);
                    ReplaceAvatarInDocument(user, path);
                }
            }
            catch (Exception e)
            {
                _logger.Warning(e, "Failed to download contributor avatar for {User}", user);
            }
            finally
            {
                lock (_downloadingAvatars)
                {
                    _ = _downloadingAvatars.Remove(user);
                }
            }
        }
    }

    /// <summary>
    /// 把弹窗已渲染文档中指定用户的占位头像图换成新下载的真头像（按 ToolTip 匹配用户）。
    /// 弹窗未打开时无文档可换，静默跳过——下次打开时 getter 会直接用磁盘上的真头像。
    /// </summary>
    /// <param name="user">GitHub 用户名。</param>
    /// <param name="avatarPath">真头像的本地路径。</param>
    private void ReplaceAvatarInDocument(string user, string avatarPath)
    {
        Execute.OnUIThread(() => {
            if (View is not Views.Dialogs.VersionUpdateDialogView view)
            {
                return;
            }

            if (view.ChangelogViewer.Document is not { } document)
            {
                return;
            }

            foreach (var image in EnumerateImages(document.Blocks))
            {
                if (image.Tag as string == "avatar" && image.ToolTip as string == user)
                {
                    image.Source = new BitmapImage(new Uri(avatarPath));
                }
            }
        });
    }

    private static IEnumerable<Paragraph> EnumerateParagraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;

                case Section section:
                    foreach (var nested in EnumerateParagraphs(section.Blocks))
                    {
                        yield return nested;
                    }

                    break;

                case List list:
                    foreach (var listItem in list.ListItems)
                    {
                        foreach (var nested in EnumerateParagraphs(listItem.Blocks))
                        {
                            yield return nested;
                        }
                    }

                    break;

                case Table table:
                    foreach (var rowGroup in table.RowGroups)
                    {
                        foreach (var row in rowGroup.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                foreach (var nested in EnumerateParagraphs(cell.Blocks))
                                {
                                    yield return nested;
                                }
                            }
                        }
                    }

                    break;

                // details 块渲染为 Expander，内容在嵌套 FlowDocumentScrollViewer 的文档里
                case BlockUIContainer { Child: Expander { Content: FlowDocumentScrollViewer nested } }:
                    if (nested.Document is not null)
                    {
                        foreach (var paragraph in EnumerateParagraphs(nested.Document.Blocks))
                        {
                            yield return paragraph;
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<Image> EnumerateImages(BlockCollection blocks)
    {
        foreach (var paragraph in EnumerateParagraphs(blocks))
        {
            foreach (var image in EnumerateImages(paragraph.Inlines))
            {
                yield return image;
            }
        }
    }

    private static IEnumerable<Image> EnumerateImages(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case InlineUIContainer { Child: Image image }:
                    yield return image;
                    break;

                case Hyperlink hyperlink:
                    foreach (var nested in EnumerateImages(hyperlink.Inlines))
                    {
                        yield return nested;
                    }

                    break;

                case Span span:
                    foreach (var nested in EnumerateImages(span.Inlines))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    // UI 预览模式下 Core 未加载，不触碰 native，降级为占位值（预览模式不做更新比较）
    private readonly string _curVersion = Main.Bootstrapper.IsCoreInitSkipped
        ? "N/A"
        : Marshal.PtrToStringAnsi(MaaService.AsstGetVersion()) ?? "0.0.1";

    /// <summary>
    /// Gets or sets the update tag.
    /// </summary>
    public string UpdateTag
    {
        get; set {
            SetAndNotify(ref field, value);
            ConfigFactory.Root.Update.Name = value;
        }
    } = ConfigFactory.Root.Update.Name;

    private static string LoadUpdateBody()
    {
        var body = MarkdownDataHelper.Get("CHANGELOG");
        if (!string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        return string.Empty;
    }

    private string _updateInfo = LoadUpdateBody();

    // private static readonly MarkdownPipeline s_markdownPipeline = new MarkdownPipelineBuilder().UseXamlSupportedExtensions().Build();

    /// <summary>
    /// Gets or sets the update info.
    /// </summary>
    public string UpdateInfo
    {
        get {
            try
            {
                return FormatUpdateInfo(_updateInfo);
            }
            catch
            {
                return _updateInfo;
            }
        }

        set {
            SetAndNotify(ref _updateInfo, value);
            MarkdownDataHelper.Set("CHANGELOG", value);
            _ = DownloadMissingContributorAvatarsAsync(value);
        }
    }

    /// <summary>
    /// Gets or sets the update URL.
    /// </summary>
    public string UpdateUrl { get; set => SetAndNotify(ref field, value); } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether it is the first boot after updating.
    /// </summary>
    public bool IsFirstBootAfterUpdate
    {
        get; set {
            SetAndNotify(ref field, value);
            ConfigFactory.Root.Update.IsFirstBoot = value;
        }
    } = ConfigFactory.Root.Update.IsFirstBoot;

    /// <summary>
    /// Gets the OS architecture.
    /// </summary>
    private static string OsArchitecture => RuntimeInformation.OSArchitecture.ToString().ToLower();

    /// <summary>
    /// Gets a value indicating whether the OS is arm.
    /// </summary>
    public static bool IsArm => OsArchitecture.StartsWith("arm");

    public enum CheckUpdateRetT
    {
        /// <summary>
        /// 操作成功
        /// </summary>
        OK,

        /// <summary>
        /// 未知错误
        /// </summary>
        UnknownError,

        /// <summary>
        /// 无需更新
        /// </summary>
        NoNeedToUpdate,

        /// <summary>
        /// 调试版本无需更新
        /// </summary>
        NoNeedToUpdateDebugVersion,

        /// <summary>
        /// 已经是最新版
        /// </summary>
        AlreadyLatest,

        /// <summary>
        /// 网络错误
        /// </summary>
        NetworkError,

        /// <summary>
        /// 获取信息失败
        /// </summary>
        FailedToGetInfo,

        /// <summary>
        /// 更新包下载失败
        /// </summary>
        UpdatePackageDownloadFailed,

        /// <summary>
        /// 新版正在构建中
        /// </summary>
        NewVersionIsBeingBuilt,

        /// <summary>
        /// 只更新了游戏资源
        /// </summary>
        OnlyGameResourceUpdated,

        /// <summary>
        /// NoMirrorChyanCdk
        /// </summary>
        NoMirrorChyanCdk,

        /// <summary>
        /// 凭据错误已由共享状态模型显示并通知。
        /// </summary>
        CredentialError,
    }

    /// <summary>
    /// Gets or sets a value indicating whether to show the update.
    /// </summary>
    public bool DoNotShowUpdate
    {
        get; set {
            SetAndNotify(ref field, value);
            ConfigFactory.Root.Update.DoNotShowUpdate = value;
        }
    } = ConfigFactory.Root.Update.DoNotShowUpdate;

    /// <summary>
    /// 显示更新日志，并按启动设置检查上游软件版本和资源更新。
    /// </summary>
    /// <returns>Task</returns>
    public async Task ShowUpdateOrDownload()
    {
        if (IsFirstBootAfterUpdate)
        {
            IsFirstBootAfterUpdate = false;
            if (!DoNotShowUpdate)
            {
                _ = DownloadMissingContributorAvatarsAsync(_updateInfo);
                Instances.WindowManager.ShowWindow(this);
            }
        }

        if (!SettingsViewModel.VersionUpdateSettings.StartupUpdateCheck)
        {
            return;
        }

        await CheckAndNotifyVersionUpdateAsync();
        await ResourceUpdater.ResourceUpdateAndReloadAsync();
    }

    /// <summary>
    /// 检查上游软件版本并提醒，不下载或安装软件更新包。
    /// </summary>
    /// <returns>Task</returns>
    public async Task CheckAndNotifyVersionUpdateAsync()
    {
        var ret = await CheckVersionUpdateAsync();
        var message = ret switch {
            CheckUpdateRetT.NetworkError => LocalizationHelper.GetString("VersionUpdate.CheckNetworking"),
            CheckUpdateRetT.FailedToGetInfo => LocalizationHelper.GetString("VersionUpdate.GetReleaseNoteFailed"),
            _ => string.Empty,
        };
        if (!string.IsNullOrEmpty(message))
        {
            ToastNotification.ShowDirect(message);
        }
    }

    /// <summary>
    /// 只查询主仓库的发布信息；软件更新源与资源更新设置无关。
    /// </summary>
    /// <returns>软件版本检查结果。</returns>
    public async Task<CheckUpdateRetT> CheckVersionUpdateAsync()
    {
        var settings = SettingsViewModel.VersionUpdateSettings;
        if (settings.IsCheckingForUpdates)
        {
            return CheckUpdateRetT.NoNeedToUpdate;
        }

        if (IsDebugVersion())
        {
            return CheckUpdateRetT.NoNeedToUpdateDebugVersion;
        }

        settings.IsCheckingForUpdates = true;
        try
        {
            var channel = settings.VersionType;
            var (release, ret) = await GitHubUpdateHelper.GetUpstreamReleaseAsync(channel);
            if (channel != settings.VersionType)
            {
                return CheckUpdateRetT.NoNeedToUpdate;
            }

            if (ret != CheckUpdateRetT.OK || release is null)
            {
                return ret;
            }

            var version = release.Value<string>("tag_name");
            if (!SemVersion.TryParse(version, SemVersionStyles.AllowLowerV, out var latestVersion))
            {
                return CheckUpdateRetT.FailedToGetInfo;
            }

            if (!NeedToUpdate(latestVersion))
            {
                settings.NewVersionFoundInfo = string.Empty;
                return CheckUpdateRetT.AlreadyLatest;
            }

            UpdateTag = version!;
            UpdateInfo = release.Value<string>("body") ?? string.Empty;
            UpdateUrl = $"{MaaUrls.GitHub}/releases/tag/{Uri.EscapeDataString(version!)}";
            settings.NewVersionFoundInfo = $"{LocalizationHelper.GetString("VersionUpdate.NewVersionFound.Title")}: {UpdateTag}";
            using var toast = new ToastNotification(settings.NewVersionFoundInfo);
            toast.AppendContentText(LocalizationHelper.GetString("Settings.Update.NotifyOnly.Tip"));
            toast.AddButton(LocalizationHelper.GetString("VersionUpdate.NewVersionFound.GoWebpage"), ToastNotification.GetActionTagForOpenWeb(UpdateUrl));
            toast.ShowUpdateVersion();
            return CheckUpdateRetT.OK;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to check upstream software releases");
            return CheckUpdateRetT.NetworkError;
        }
        finally
        {
            settings.IsCheckingForUpdates = false;
        }
    }

    private bool NeedToUpdate(SemVersion latestVersion)
    {
        return SemVersion.TryParse(_curVersion, SemVersionStyles.AllowLowerV, out var currentVersion)
            && currentVersion.ComparePrecedenceTo(latestVersion) < 0;
    }

    public static void OutputDownloadProgress(long value = 0, long maximum = 1, int len = 0, double ts = 1, string? toolTip = null)
    {
        string progress = $"[{value / 1048576.0:F}MiB/{maximum / 1048576.0:F}MiB ({value * 100.0 / maximum:F}%)";

        double speedInKiBPerSecond = len / ts / 1024.0;

        var speedDisplay = speedInKiBPerSecond >= 1024
            ? $"{speedInKiBPerSecond / 1024.0:F} MiB/s"
            : $"{speedInKiBPerSecond:F} KiB/s";

        OutputDownloadProgress(progress + $" {speedDisplay}", toolTip: toolTip);
    }

    private static bool _globalSource = true;

    public static void OutputDownloadProgress(string output, bool downloading = true, bool? globalSource = null, string? toolTip = null)
    {
        globalSource ??= _globalSource;
        _globalSource = globalSource.Value;

        string fullText;
        if (downloading)
        {
            string key = globalSource.Value ? "VersionUpdate.NewVersionDownloading.GlobalSource" : "VersionUpdate.NewVersionDownloading.MirrorChyan";
            fullText = LocalizationHelper.GetString(key) + "\n" + output;
        }
        else
        {
            fullText = output;
        }

        Instances.TaskQueueViewModel?.UpdateDownloadLog(fullText, toolTip);
    }

    public bool IsDebugVersion(string? version = null)
    {
        // return false;
        version ??= _curVersion;

        // match case 1: DEBUG_VERSION
        // match case 2: v{Major}.{Minor}.{Patch}-{CommitDistance}-g{CommitHash}
        // match case 3: {CommitHash}
        return Regex.IsMatch(version, @"^(.*DEBUG.*|v\d+(\.\d+){1,3}-\d+-g[0-9a-f]{6,}|[^v][0-9a-f]{6,})$");
    }

    public bool IsStdVersion(string? version = null)
    {
        // 正式版：vX.X.X
        // DevBuild (CI)：yyyy-MM-dd-HH-mm-ss-{CommitHash[..7]}
        // DevBuild (Local)：yyyy-MM-dd-HH-mm-ss-{CommitHash[..7]}-Local
        // Release (Local Commit)：v.{CommitHash[..7]}-Local
        // Release (Local Tag)：{Tag}-Local
        // Debug (Local)：DEBUG_VERSION
        // Script Compiled：c{CommitHash[..7]}
        version ??= _curVersion;

        if (IsDebugVersion(version))
        {
            return false;
        }

        if (version.StartsWith('c') || version.StartsWith("20") || version.Contains("Local"))
        {
            return false;
        }

        if (!SemVersion.TryParse(version, SemVersionStyles.AllowLowerV, out var semVersion))
        {
            return false;
        }

        return !semVersion.IsPrerelease;
    }

    public bool IsBetaVersion(string? version = null)
    {
        version ??= _curVersion;

        if (IsDebugVersion(version))
        {
            return false;
        }

        if (version.StartsWith('c') || version.StartsWith("20") || version.Contains("Local"))
        {
            return false;
        }

        if (!SemVersion.TryParse(version, SemVersionStyles.AllowLowerV, out var semVersion))
        {
            return false;
        }

        return semVersion.IsPrerelease && !IsNightlyVersion(semVersion);
    }

    public static bool IsNightlyVersion(SemVersion version)
    {
        if (!version.IsPrerelease)
        {
            return false;
        }

        // v{Major}.{Minor}.{Patch}-{Prerelease}.{CommitDistance}.g{CommitHash}
        // v4.6.7-beta.2.1.g1234567
        // v4.6.8-5.g1234567
        var lastId = version.PrereleaseIdentifiers.LastOrDefault().ToString();
        return lastId.StartsWith('g') && lastId.Length >= 7;
    }

    [GeneratedRegex(@"(?<=<details\s)open(?=[\s>])")]
    private static partial Regex MyRegex();
}
