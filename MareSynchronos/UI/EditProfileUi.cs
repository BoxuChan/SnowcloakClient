﻿using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using MareSynchronos.API.Data;
using MareSynchronos.API.Dto.User;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.Utils;
using MareSynchronos.WebAPI;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.UI;

public class EditProfileUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly FileDialogManager _fileDialogManager;
    private readonly MareProfileManager _mareProfileManager;
    private readonly UiSharedService _uiSharedService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private bool _adjustedForScollBarsLocalProfile = false;
    private bool _adjustedForScollBarsOnlineProfile = false;
    private string _descriptionText = string.Empty;
    private IDalamudTextureWrap? _pfpTextureWrap;
    private string _profileDescription = string.Empty;
    private byte[] _profileImage = [];
    private string _showFileDialogError = string.Empty;
    private bool _wasOpen;

    public EditProfileUi(ILogger<EditProfileUi> logger, MareMediator mediator,
        ApiController apiController, UiSharedService uiSharedService, FileDialogManager fileDialogManager,
        ServerConfigurationManager serverConfigurationManager,
        MareProfileManager mareProfileManager, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Profile Editor###SnowcloakSyncEditProfileUI", performanceCollectorService)
    {
        IsOpen = false;
        this.SizeConstraints = new()
        {
            MinimumSize = new(768, 512),
            MaximumSize = new(768, 2000)
        };
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _fileDialogManager = fileDialogManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mareProfileManager = mareProfileManager;

        Mediator.Subscribe<GposeStartMessage>(this, (_) => { _wasOpen = IsOpen; IsOpen = false; });
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = _wasOpen);
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData == null || string.Equals(msg.UserData.UID, _apiController.UID, StringComparison.Ordinal))
            {
                _pfpTextureWrap?.Dispose();
                _pfpTextureWrap = null;
            }
        });
    }

    protected override void DrawInternal()
    {
        _uiSharedService.BigText("Current Profile (as saved on server)");

        var profile = _mareProfileManager.GetMareProfile(new UserData(_apiController.UID));

        if (profile.IsFlagged)
        {
            UiSharedService.ColorTextWrapped(profile.Description, ImGuiColors.DalamudRed);
            return;
        }

        if (!_profileImage.SequenceEqual(profile.ImageData.Value))
        {
            _profileImage = profile.ImageData.Value;
            _pfpTextureWrap?.Dispose();
            _pfpTextureWrap = _uiSharedService.LoadImage(_profileImage);
        }

        if (!string.Equals(_profileDescription, profile.Description, StringComparison.OrdinalIgnoreCase))
        {
            _profileDescription = profile.Description;
            _descriptionText = _profileDescription;
        }

        if (_pfpTextureWrap != null)
        {
            ImGui.Image(_pfpTextureWrap.Handle, ImGuiHelpers.ScaledVector2(_pfpTextureWrap.Width, _pfpTextureWrap.Height));
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X + 200;
        ImGuiHelpers.ScaledRelativeSameLine(256, spacing);
        using (_uiSharedService.GameFont.Push())
        {
            var descriptionTextSize = ImGui.CalcTextSize(profile.Description, hideTextAfterDoubleHash: false, 256f);
            var childFrame = ImGuiHelpers.ScaledVector2(256 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, 256);
            _adjustedForScollBarsOnlineProfile = (descriptionTextSize.Y > childFrame.Y);
            childFrame = childFrame with
            {
                X = childFrame.X + (_adjustedForScollBarsOnlineProfile ? ImGui.GetStyle().ScrollbarSize : 0),
            };
            if (ImGui.BeginChildFrame(101, childFrame))
            {
                UiSharedService.TextWrapped(profile.Description);
            }
            ImGui.EndChildFrame();
        }

        var nsfw = profile.IsNSFW;
        ImGui.BeginDisabled();
        ImGui.Checkbox("Is NSFW", ref nsfw);
        ImGui.EndDisabled();

        ImGui.Separator();
        _uiSharedService.BigText("Rules and Guidelines");
        UiSharedService.ColorTextWrapped("Users that are paired with you (not paused) will be able to see your profile picture and description.", ImGuiColors.DalamudWhite);
        UiSharedService.ColorTextWrapped("All users have the capability to report your profile if it violates the rules.", ImGuiColors.DalamudGrey);
        UiSharedService.ColorTextWrapped(" - Please do NOT upload anything that can be considered highly illegal or obscene (beastiality, sexual acts depicting minors or anything representing a minor (including Lalafel), etc.)", ImGuiColors.DalamudRed);
        UiSharedService.ColorTextWrapped(" - Please avoid the use of slurs, hate speech, threatening behaviour, etc.", ImGuiColors.DalamudRed);
        UiSharedService.ColorTextWrapped(" - In the event we receive a report of an offensive profile, we may disable your profile forever or terminate your Snowcloak service account.", ImGuiColors.DalamudRed);
        UiSharedService.ColorTextWrapped(" - You may not appeal any bans of your profile and or Snowcloak service account.", ImGuiColors.DalamudRed);
        UiSharedService.ColorTextWrapped("Users who wish to mark their profile as NSFW should enable the toggle below.", ImGuiColors.DalamudWhite);
        ImGui.Separator();
        _uiSharedService.BigText("Profile Settings");
        UiSharedService.ColorTextWrapped("Profile pictures must be cropped to 256x256px and have a file size of 250KiB or smaller.", ImGuiColors.DalamudGrey);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileUpload, "Upload new profile picture"))
        {
            _fileDialogManager.OpenFileDialog("Select new Profile picture", ".png", (success, file) =>
            {
                if (!success) return;
                _ = Task.Run(async () =>
                {
                    var fileContent = File.ReadAllBytes(file);
                    using MemoryStream ms = new(fileContent);
                    var format = PngHdr.TryExtractDimensions(ms);

                    if (format.Width > 256 || format.Height > 256 || (fileContent.Length > 250 * 1024))
                    {
                        _showFileDialogError = format.Width > 256 || format.Height > 256 ? "ERROR: Image dimensions must be 256x256px or smaller." : fileContent.Length > 250 * 1024 ? "ERROR: File size was bigger than 250KiB" : "ERROR: An unknown error has occured.";
                        return;
                    }
                    _showFileDialogError = string.Empty;
                    await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, Convert.ToBase64String(fileContent), Description: null))
                        .ConfigureAwait(false);
                });
            });
        }
        UiSharedService.AttachToolTip("Select and upload a new profile picture");
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear uploaded profile picture"))
        {
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, "", Description: null));
        }
        UiSharedService.AttachToolTip("Clear your currently uploaded profile picture");
        if (!_showFileDialogError.IsNullOrEmpty())
        {
            UiSharedService.ColorTextWrapped(_showFileDialogError, ImGuiColors.DalamudRed);
        }
        var isNsfw = profile.IsNSFW;
        if (ImGui.Checkbox("Profile is NSFW", ref isNsfw))
        {
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, isNsfw, ProfilePictureBase64: null, Description: null));
        }
        _uiSharedService.DrawHelpText("If your profile description or image can be considered NSFW, toggle this to ON");
        var widthTextBox = 400;
        var posX = ImGui.GetCursorPosX();
        ImGui.TextUnformatted($"Description {_descriptionText.Length}/1500");
        ImGui.SetCursorPosX(posX);
        ImGuiHelpers.ScaledRelativeSameLine(widthTextBox, ImGui.GetStyle().ItemSpacing.X);
        ImGui.TextUnformatted("Preview (approximate)");
        using (_uiSharedService.GameFont.Push())
            ImGui.InputTextMultiline("##description", ref _descriptionText, 1500, ImGuiHelpers.ScaledVector2(widthTextBox, 200));

        ImGui.SameLine();

        using (_uiSharedService.GameFont.Push())
        {
            var descriptionTextSizeLocal = ImGui.CalcTextSize(_descriptionText, hideTextAfterDoubleHash: false, 256f);
            var childFrameLocal = ImGuiHelpers.ScaledVector2(256 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, 200);
            if (descriptionTextSizeLocal.Y > childFrameLocal.Y)
            {
                _adjustedForScollBarsLocalProfile = true;
            }
            else
            {
                _adjustedForScollBarsLocalProfile = false;
            }
            childFrameLocal = childFrameLocal with
            {
                X = childFrameLocal.X + (_adjustedForScollBarsLocalProfile ? ImGui.GetStyle().ScrollbarSize : 0),
            };
            if (ImGui.BeginChildFrame(102, childFrameLocal))
            {
                UiSharedService.TextWrapped(_descriptionText);
            }
            ImGui.EndChildFrame();
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Save Description"))
        {
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, ProfilePictureBase64: null, _descriptionText));
        }
        UiSharedService.AttachToolTip("Sets your profile description text");
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear Description"))
        {
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, ProfilePictureBase64: null, ""));
        }
        UiSharedService.AttachToolTip("Clears your profile description text");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _pfpTextureWrap?.Dispose();
    }
}