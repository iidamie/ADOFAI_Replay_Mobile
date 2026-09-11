using System.Runtime.InteropServices;
using System.Text.Json;
using ImGuiNET;
using StArray.ModManager.Il2Cpp;
using StArray.ModManager.Manager;
using StArray.ModManager.Mono;
using StArray.ModManager.RuntimeAbstractions;

namespace Replay.Mobile;

/// <summary>
/// Loads a chart preview image through the game's Unity Texture2D path. The
/// texture is created lazily from the foreground GUI, which keeps
/// Unity object access off the replay/network state code.
/// </summary>
internal sealed unsafe class ReplayChartPreview : IDisposable
{
    private const long MaxPreviewBytes = 16L * 1024L * 1024L;

    private readonly IAppDomain _domain;
    private readonly IRuntimeAssembly _assembly;
    private readonly IRuntimeClass? _officialTileClass;
    private readonly IRuntimeClass? _officialPortalClass;
    private readonly IRuntimeClass? _spriteClass;
    private readonly IRuntimeClass? _spriteRendererClass;
    private readonly IRuntimeClass? _graphicsClass;
    private readonly IRuntimeClass? _renderTextureClass;
    private readonly IRuntimeClass? _resourcesClass;
    private readonly IRuntimeClass? _textAssetClass;
    private readonly IRuntimeClass? _texture2DClass;
    private readonly IRuntimeClass? _textureClass;
    private readonly IRuntimeClass? _imageConversionClass;
    private readonly IRuntimeClass? _objectClass;
    private readonly IRuntimeClass? _byteClass;
    private readonly IRuntimeClass? _systemInfoClass;
    private readonly IRuntimeField? _officialTileWorld;
    private readonly IRuntimeField? _officialTileLevelIcon;
    private readonly IRuntimeField? _officialTileSpeedTrial;
    private readonly IRuntimeField? _officialPortalWorld;
    private readonly IRuntimeField? _officialPortalRenderer;
    private readonly IRuntimeMethod? _textureConstructor;
    private readonly IRuntimeMethod? _loadImage;
    private readonly IRuntimeMethod? _loadImageWithReadableFlag;
    private readonly IRuntimeMethod? _encodeToPng;
    private readonly IRuntimeMethod? _textureWidth;
    private readonly IRuntimeMethod? _textureHeight;
    private readonly IRuntimeMethod? _getNativeTexturePtr;
    private readonly IRuntimeMethod? _destroyObject;
    private readonly IRuntimeMethod? _unityObjectImplicit;
    private readonly IRuntimeMethod? _getGraphicsDeviceType;
    private readonly IRuntimeMethod? _findObjectsOfType;
    private readonly IRuntimeMethod? _findObjectsOfTypeAll;
    private readonly IRuntimeMethod? _resourcesLoad;
    private readonly IRuntimeMethod? _textAssetGetText;
    private readonly IRuntimeMethod? _spriteRendererGetSprite;
    private readonly IRuntimeMethod? _graphicsBlit;
    private readonly IRuntimeMethod? _renderTextureConstructor;
    private readonly IRuntimeMethod? _renderTextureCreate;
    private readonly IRuntimeMethod? _renderTextureRelease;
    private readonly IRuntimeMethod? _renderTextureActiveGetter;
    private readonly IRuntimeMethod? _renderTextureActiveSetter;
    private readonly IRuntimeMethod? _textureReadPixels;
    private readonly IRuntimeMethod? _textureApply;
    private readonly IRuntimeMethod? _spriteGetTexture;
    private readonly nint _officialTileTypeObject;
    private readonly nint _officialPortalTypeObject;
    private readonly nint _spriteTypeObject;
    private readonly nint _texture2DTypeObject;
    private readonly nint _textAssetTypeObject;

    private string _previewPath = "";
    private nint _texture;
    private nint _textureHandle;
    private nint _textureId;
    private int _width;
    private int _height;
    private bool _isOpenGlTexture;
    private IntPtr _textureContext;
    private bool _borrowedTexture;
    private byte[]? _officialPngData;
    private bool _loadFailed;

    internal bool HasSource => !string.IsNullOrWhiteSpace(_previewPath)
        || _borrowedTexture && _textureId != 0
        || _officialPngData is { Length: > 0 }
        || _isOpenGlTexture && _textureId != 0;

    internal ReplayChartPreview(IAppDomain domain, IRuntimeAssembly assembly)
    {
        _domain = domain;
        _assembly = assembly;
        _officialTileClass = FindClassInDomain(domain, "", "WorldSelectorTile")
            ?? assembly.GetClass("", "WorldSelectorTile");
        _officialPortalClass = FindClassInDomain(domain, "", "scrPortal")
            ?? assembly.GetClass("", "scrPortal");
        _spriteClass = FindClassInDomain(domain, "UnityEngine", "Sprite");
        _spriteRendererClass = FindClassInDomain(domain, "UnityEngine", "SpriteRenderer");
        _graphicsClass = FindClassInDomain(domain, "UnityEngine", "Graphics");
        _renderTextureClass = FindClassInDomain(domain, "UnityEngine", "RenderTexture");
        _resourcesClass = FindClassInDomain(domain, "UnityEngine", "Resources");
        _textAssetClass = FindClassInDomain(domain, "UnityEngine", "TextAsset");
        _texture2DClass = FindClassInDomain(domain, "UnityEngine", "Texture2D");
        _textureClass = FindClassInDomain(domain, "UnityEngine", "Texture") ?? _texture2DClass;
        _imageConversionClass = FindClassInDomain(domain, "UnityEngine", "ImageConversion");
        _objectClass = FindClassInDomain(domain, "UnityEngine", "Object");
        _byteClass = FindClassInDomain(domain, "System", "Byte");
        _systemInfoClass = FindClassInDomain(domain, "UnityEngine", "SystemInfo");
        _officialTileWorld = FindField(_officialTileClass, "world");
        _officialTileLevelIcon = FindField(_officialTileClass, "levelIcon");
        _officialTileSpeedTrial = FindField(_officialTileClass, "speedTrial");
        _officialPortalWorld = FindField(_officialPortalClass, "world");
        _officialPortalRenderer = FindField(_officialPortalClass, "sprPortal");
        _graphicsBlit = _graphicsClass?.GetMethod(
                "Blit",
                new[] { "UnityEngine.Texture", "UnityEngine.RenderTexture" })
            ?? _graphicsClass?.GetMethod("Blit", 2);
        _renderTextureConstructor = _renderTextureClass?.GetMethod(
                ".ctor",
                new[]
                {
                    "System.Int32",
                    "System.Int32",
                    "System.Int32",
                    "UnityEngine.RenderTextureFormat",
                })
            ?? _renderTextureClass?.GetMethod(
                ".ctor",
                new[] { "System.Int32", "System.Int32", "System.Int32" })
            ?? _renderTextureClass?.GetMethod(".ctor", 4)
            ?? _renderTextureClass?.GetMethod(".ctor", 3);
        _renderTextureCreate = _renderTextureClass?.GetMethod("Create", 0);
        _renderTextureRelease = _renderTextureClass?.GetMethod("Release", 0);
        _renderTextureActiveGetter = _renderTextureClass?.GetMethod("get_active", 0);
        _renderTextureActiveSetter = _renderTextureClass?.GetMethod("set_active", 1);
        _textureConstructor = _texture2DClass?.GetMethod(
                ".ctor",
                new[]
                {
                    "System.Int32",
                    "System.Int32",
                    "UnityEngine.TextureFormat",
                    "System.Boolean",
                })
            ?? _texture2DClass?.GetMethod(".ctor", 4);
        _loadImage = _imageConversionClass?.GetMethod("LoadImage", 2);
        _loadImageWithReadableFlag = _imageConversionClass?.GetMethod("LoadImage", 3);
        _encodeToPng = _imageConversionClass?.GetMethod(
                "EncodeToPNG",
                new[] { "UnityEngine.Texture2D" })
            ?? _imageConversionClass?.GetMethod("EncodeToPNG", 1);
        _textureWidth = _texture2DClass?.GetMethod("get_width", 0)
            ?? _textureClass?.GetMethod("get_width", 0);
        _textureHeight = _texture2DClass?.GetMethod("get_height", 0)
            ?? _textureClass?.GetMethod("get_height", 0);
        _getNativeTexturePtr = _textureClass?.GetMethod("GetNativeTexturePtr", 0)
            ?? _textureClass?.GetMethod("get_nativeTexturePtr", 0)
            ?? _texture2DClass?.GetMethod("GetNativeTexturePtr", 0);
        _destroyObject = _objectClass?.GetMethod("Destroy", 1);
        _unityObjectImplicit = _objectClass?.GetMethod("op_Implicit", 1);
        _getGraphicsDeviceType = _systemInfoClass?.GetMethod("get_graphicsDeviceType", 0);
        _findObjectsOfType = _objectClass?.GetMethod(
                "FindObjectsOfType",
                new[] { "System.Type" })
            ?? _objectClass?.GetMethod("FindObjectsOfType", 1);
        _findObjectsOfTypeAll = _resourcesClass?.GetMethod(
            "FindObjectsOfTypeAll",
            new[] { "System.Type" });
        _resourcesLoad = _resourcesClass?.GetMethod(
            "Load",
            new[] { "System.String", "System.Type" });
        _textAssetGetText = _textAssetClass?.GetMethod("get_text", 0);
        _spriteRendererGetSprite = _spriteRendererClass?.GetMethod("get_sprite", 0);
        _textureReadPixels = _texture2DClass?.GetMethod("ReadPixels", 4);
        _textureApply = _texture2DClass?.GetMethod("Apply", 2)
            ?? _texture2DClass?.GetMethod("Apply", 1)
            ?? _texture2DClass?.GetMethod("Apply", 0);
        _spriteGetTexture = _spriteClass?.GetMethod("get_texture", 0);
        _officialTileTypeObject = GetRuntimeTypeObject(_officialTileClass);
        _officialPortalTypeObject = GetRuntimeTypeObject(_officialPortalClass);
        _spriteTypeObject = GetRuntimeTypeObject(_spriteClass);
        _texture2DTypeObject = GetRuntimeTypeObject(_texture2DClass);
        _textAssetTypeObject = GetRuntimeTypeObject(_textAssetClass);
        Logger.Info(
            "Replay",
            "官谱封面解析器: WorldSelectorTile=0x"
            + _officialTileClass?.Ptr.ToString("X")
            + ", type=0x" + _officialTileTypeObject.ToString("X")
            + ", scrPortal=0x" + _officialPortalClass?.Ptr.ToString("X")
            + ", portalType=0x" + _officialPortalTypeObject.ToString("X")
            + ", spriteType=0x" + _spriteTypeObject.ToString("X")
            + ", graphics=" + (_graphicsBlit != null)
            + ", renderTexture=" + (_renderTextureClass != null)
            + ", readPixels=" + (_textureReadPixels != null)
            + ", apply=" + (_textureApply != null)
            + ", findObjects=" + (_findObjectsOfType != null)
            + ", findAll=" + (_findObjectsOfTypeAll != null)
            + ", resourcesLoad=" + (_resourcesLoad != null)
            + ", textAssetText=" + (_textAssetGetText != null)
            + ", textureType=0x" + _texture2DTypeObject.ToString("X")
            + ", textAssetType=0x" + _textAssetTypeObject.ToString("X")
            + ", spriteTexture=" + (_spriteGetTexture != null)
            + ", worldField=" + (_officialTileWorld != null)
            + ", iconField=" + (_officialTileLevelIcon != null)
            + ", speedTrialField=" + (_officialTileSpeedTrial != null)
            + ", portalWorldField=" + (_officialPortalWorld != null)
            + ", portalRendererField=" + (_officialPortalRenderer != null)
            + ", rendererSprite=" + (_spriteRendererGetSprite != null)
            + ", encodeToPng=" + (_encodeToPng != null)
            + ", nativeTexture=" + (_getNativeTexturePtr != null));
    }

    internal void SetChart(string chartPath)
    {
        ReplayChartPreviewSource? source = ReplayChartMetadata.PreparePreviewImage(chartPath);
        string previewPath = source?.Path ?? "";
        if (string.Equals(_previewPath, previewPath, StringComparison.OrdinalIgnoreCase)
            && !_borrowedTexture
            && _officialPngData == null
            && !_isOpenGlTexture)
            return;

        ReleaseTexture();
        _officialPngData = null;
        _previewPath = previewPath;
        _loadFailed = false;
    }

    /// <summary>
    /// Official levels normally display their world artwork from the game's
    /// scrPortal.sprPortal renderer. Extra/special worlds additionally use
    /// WorldSelectorTile.levelIcon. Both paths borrow the already-loaded Unity
    /// texture and only encode it when the mobile backend does not expose a
    /// native texture handle.
    /// </summary>
    internal void SetOfficialLevel(string levelId)
    {
        ReleaseTexture();
        _officialPngData = null;
        _previewPath = "";
        _loadFailed = false;
        string target = levelId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            if (TryLoadOfficialResource(target))
                return;

            // Normal official worlds are represented by scrPortal objects in
            // the game's own level-select scene. Their sprPortal sprite is
            // the same artwork the game displays for the official portal.
            // WorldSelectorTile is only used by extra/special worlds, so it
            // cannot resolve ids such as 1-X or 2-X by itself.
            if (TryUseOfficialPortals(target))
                return;

            if (_officialTileTypeObject == 0
                || _findObjectsOfType == null
                || _officialTileWorld == null
                || _officialTileLevelIcon == null
                || _spriteGetTexture == null)
            {
                return;
            }

            nint objects = _findObjectsOfType.InvokeStatic(new[] { _officialTileTypeObject });
            Logger.Info(
                "Replay",
                "请求官谱封面: id=" + target
                + ", activeObjects=" + GetArrayLength(objects));
            if (TryUseOfficialTiles(objects, target, out bool found) && found)
            {
                return;
            }
            if (_findObjectsOfTypeAll != null)
            {
                nint allObjects = _findObjectsOfTypeAll.InvokeStatic(new[] { _officialTileTypeObject });
                Logger.Info(
                    "Replay",
                    "请求官谱封面资源回退: id=" + target
                    + ", allObjects=" + GetArrayLength(allObjects));
                if (TryUseOfficialTiles(allObjects, target, out found))
                    return;
            }
            Logger.Debug(
                "Replay",
                "未找到官谱 WorldSelectorTile.levelIcon: id=" + target
                + ", tileType=0x" + _officialTileTypeObject.ToString("X"));
        }
        catch (Exception exception)
        {
            Logger.Debug("Replay", "读取官谱封面失败: " + exception.Message);
        }
    }

    private bool TryLoadOfficialResource(string target)
    {
        if (_resourcesLoad == null
            || _textAssetGetText == null
            || _texture2DTypeObject == 0
            || _textAssetTypeObject == 0)
        {
            return false;
        }

        string normalized = NormalizeOfficialId(target);
        int separator = normalized.IndexOf('-');
        if (separator <= 0 || separator >= normalized.Length - 1)
            return false;
        string worldNumber = normalized[..separator];
        string[] worldCandidates = GetOfficialWorldCandidates(worldNumber);
        Logger.Info(
            "Replay",
            "官谱内置资源查找: level=" + target
            + ", worlds=" + string.Join(",", worldCandidates));

        foreach (string world in worldCandidates)
        {
            foreach (string levelName in GetOfficialLevelResourceNames(normalized[separator..]))
            {
                string levelResource = "InternalLevels/" + world + "/" + levelName;
                nint textAsset = LoadResource(levelResource, _textAssetTypeObject);
                if (textAsset == 0)
                    continue;

                string json = InvokeInstanceString(_textAssetGetText, textAsset);
                string previewImage = ReadOfficialPreviewImage(json);
                string textureName = GetResourceFileName(previewImage);
                Logger.Debug(
                    "Replay",
                    "官谱关卡资源命中: levelResource=" + levelResource
                    + ", previewImage=" + previewImage
                    + ", textureName=" + textureName);
                if (string.IsNullOrWhiteSpace(textureName))
                    continue;

                string textureResource = "InternalLevels/" + world + "/" + textureName;
                nint texture = LoadResource(textureResource, _texture2DTypeObject);
                if (texture == 0)
                    continue;

                if (ApplyOfficialTexture(texture, target, "TextureManager.LoadTexture"))
                {
                    Logger.Info(
                        "Replay",
                        "已按官谱内置资源路径加载封面: level=" + target
                        + ", levelResource=" + levelResource
                        + ", textureResource=" + textureResource);
                    return true;
                }
            }
        }
        Logger.Debug("Replay", "官谱内置资源未命中: level=" + target);
        return false;
    }

    private static string[] GetOfficialWorldCandidates(string worldNumber)
        => string.IsNullOrWhiteSpace(worldNumber)
            ? Array.Empty<string>()
            : new[] { worldNumber };

    private bool TryUseOfficialPortals(string target)
    {
        string worldKey = GetOfficialWorldKey(target);
        if (string.IsNullOrWhiteSpace(worldKey))
            return false;

        if (_officialPortalTypeObject != 0
            && _officialPortalWorld != null
            && _officialPortalRenderer != null
            && _spriteRendererGetSprite != null
            && _findObjectsOfType != null)
        {
            nint activeObjects = _findObjectsOfType.InvokeStatic(new[] { _officialPortalTypeObject });
            Logger.Info(
                "Replay",
                "请求官谱 Portal 封面: id=" + target
                + ", world=" + worldKey
                + ", activeObjects=" + GetArrayLength(activeObjects));
            if (TryUseOfficialPortalArray(activeObjects, worldKey, target))
                return true;
        }

        if (_officialPortalTypeObject != 0
            && _officialPortalWorld != null
            && _officialPortalRenderer != null
            && _spriteRendererGetSprite != null
            && _findObjectsOfTypeAll != null)
        {
            nint allObjects = _findObjectsOfTypeAll.InvokeStatic(new[] { _officialPortalTypeObject });
            Logger.Info(
                "Replay",
                "请求官谱 Portal 封面资源回退: id=" + target
                + ", world=" + worldKey
                + ", allObjects=" + GetArrayLength(allObjects));
            if (TryUseOfficialPortalArray(allObjects, worldKey, target))
                return true;
        }

        // The same official artwork is also shipped as a Resources sprite.
        // This is the path used by scrPortal for the special-world fallback
        // and remains available when the level-select scene is not loaded.
        return TryLoadOfficialPortalResource(worldKey, target);
    }

    private bool TryUseOfficialPortalArray(nint objects, string worldKey, string target)
    {
        RuntimeArray portals = new(objects);
        if (!portals.IsValid)
            return false;

        List<string> worldSamples = new();
        for (int index = 0; index < portals.Length; index++)
        {
            nint portal = portals[index];
            if (portal == 0)
                continue;

            string world = ReadString(_officialPortalWorld, portal);
            if (worldSamples.Count < 20)
                worldSamples.Add(world);
            if (!string.Equals(world, worldKey, StringComparison.OrdinalIgnoreCase))
                continue;

            nint renderer = Read(_officialPortalRenderer, portal, nint.Zero);
            nint sprite = InvokeInstanceObject(_spriteRendererGetSprite, renderer);
            nint texture = InvokeInstanceObject(_spriteGetTexture, sprite);
            if (texture == 0)
                continue;

            if (ApplyOfficialTexture(texture, target, "scrPortal.sprPortal"))
            {
                Logger.Info(
                    "Replay",
                    "已复用官谱 Portal 封面: id=" + target
                    + ", world=" + worldKey
                    + ", portal=0x" + portal.ToString("X"));
                return true;
            }
        }

        if (worldSamples.Count > 0)
        {
            Logger.Debug(
                "Replay",
                "官谱 Portal world 样本: id=" + target
                + ", values=" + string.Join(",", worldSamples));
        }
        return false;
    }

    private bool TryLoadOfficialPortalResource(string worldKey, string target)
    {
        if (_resourcesLoad == null || _spriteTypeObject == 0)
            return false;

        string resourcePath = "InternalLevels/" + worldKey + "/portal";
        nint sprite = LoadResource(resourcePath, _spriteTypeObject);
        nint texture = InvokeInstanceObject(_spriteGetTexture, sprite);
        if (texture == 0)
        {
            // Some builds serialize the portal asset as Texture2D instead of
            // Sprite. Keep the type-specific load explicit to avoid relying
            // on Unity's implicit conversions.
            texture = LoadResource(resourcePath, _texture2DTypeObject);
        }
        if (texture == 0 || !ApplyOfficialTexture(texture, target, resourcePath))
            return false;

        Logger.Info(
            "Replay",
            "已按官谱 Portal 资源加载封面: id=" + target
            + ", resource=" + resourcePath);
        return true;
    }

    private static string GetOfficialWorldKey(string target)
    {
        string normalized = NormalizeOfficialId(target);
        int separator = normalized.IndexOf('-');
        return separator > 0 ? normalized[..separator] : normalized;
    }

    private nint LoadResource(string resourcePath, nint typeObject)
    {
        if (_resourcesLoad == null || typeObject == 0)
            return 0;
        RuntimeString key = RuntimeString.New(_domain, resourcePath);
        if (!key.IsValid)
            return 0;
        try { return _resourcesLoad.InvokeStatic(new[] { key.Ptr, typeObject }); }
        catch { return 0; }
    }

    private static IEnumerable<string> GetOfficialLevelResourceNames(string suffix)
    {
        string value = suffix.TrimStart('-');
        if (int.TryParse(value, out int number))
        {
            if (number > 0)
                yield return "sub" + number;
            else
                yield return "main";
            yield return "main";
            yield break;
        }

        if (string.Equals(value, "X", StringComparison.OrdinalIgnoreCase))
        {
            yield return "main";
            for (int index = 1; index <= 32; index++)
                yield return "sub" + index;
            yield break;
        }

        yield return "main";
        for (int index = 1; index <= 32; index++)
            yield return "sub" + index;
    }

    private static string ReadOfficialPreviewImage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
            if (!document.RootElement.TryGetProperty("settings", out JsonElement settings)
                || settings.ValueKind != JsonValueKind.Object
                || !settings.TryGetProperty("previewImage", out JsonElement value)
                || value.ValueKind != JsonValueKind.String)
            {
                return "";
            }
            return value.GetString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetResourceFileName(string value)
    {
        string normalized = value?.Trim().Replace('\\', '/') ?? "";
        int slash = normalized.LastIndexOf('/');
        if (slash >= 0)
            normalized = normalized[(slash + 1)..];
        int extension = normalized.LastIndexOf('.');
        if (extension > 0)
            normalized = normalized[..extension];
        return normalized;
    }

    private bool TryUseOfficialTiles(nint objects, string target, out bool found)
    {
        found = false;
        RuntimeArray tiles = new(objects);
        if (!tiles.IsValid)
            return false;

        nint fallbackTile = 0;
        List<string> worldSamples = new();
        for (int index = 0; index < tiles.Length; index++)
        {
            nint tile = tiles[index];
            if (tile == 0)
                continue;
            string world = ReadString(_officialTileWorld, tile);
            if (worldSamples.Count < 16)
            {
                bool speedTrial = Read(_officialTileSpeedTrial, tile, (byte)0) != 0;
                worldSamples.Add(world + (speedTrial ? "-X" : ""));
            }
            if (string.Equals(world, target, StringComparison.OrdinalIgnoreCase))
            {
                found = ApplyOfficialTile(tile, target);
                return true;
            }
            if (fallbackTile == 0 && OfficialIdsEqualIgnoringSpeedTrial(world, target))
                fallbackTile = tile;
        }

        if (fallbackTile != 0)
        {
            found = ApplyOfficialTile(fallbackTile, target);
            return true;
        }
        if (worldSamples.Count > 0)
        {
            Logger.Debug(
                "Replay",
                "官谱封面 tile world 样本: id=" + target
                + ", values=" + string.Join(",", worldSamples));
        }
        return false;
    }

    private bool ApplyOfficialTile(nint tile, string target)
    {
        nint sprite = Read(_officialTileLevelIcon, tile, nint.Zero);
        nint texture = InvokeInstanceObject(_spriteGetTexture, sprite);
        if (texture == 0)
        {
            Logger.Debug(
                "Replay",
                "官谱 WorldSelectorTile.levelIcon 没有纹理: id=" + target
                + ", tile=0x" + tile.ToString("X")
                + ", sprite=0x" + sprite.ToString("X"));
            return false;
        }
        return ApplyOfficialTexture(texture, target, "WorldSelectorTile.levelIcon");
    }

    private bool ApplyOfficialTexture(nint texture, string target, string source)
    {
        nint nativeTexture = InvokeInstanceUnbox<nint>(_getNativeTexturePtr, texture);
        int width = InvokeInstanceUnbox<int>(_textureWidth, texture);
        int height = InvokeInstanceUnbox<int>(_textureHeight, texture);
        if (texture == 0 || width <= 0 || height <= 0)
        {
            Logger.Debug(
                "Replay",
                "官谱封面纹理无效: id=" + target
                + ", source=" + source
                + ", texture=0x" + texture.ToString("X")
                + ", width=" + width
                + ", height=" + height);
            return false;
        }

        if (nativeTexture != 0)
        {
            _texture = texture;
            _textureId = nativeTexture;
            _width = width;
            _height = height;
            _borrowedTexture = true;
            Logger.Info("Replay", "已复用官谱纹理: id=" + target + ", source=" + source);
            return true;
        }

        byte[]? png = null;
        try
        {
            nint encoded = _encodeToPng?.InvokeStatic(new[] { texture }) ?? 0;
            png = ReadByteArray(encoded);
        }
        catch (Exception exception)
        {
            Logger.Debug(
                "Replay",
                "官谱纹理直接 EncodeToPNG 不可用，将尝试 GPU 读回: id=" + target
                + ", source=" + source
                + ", reason=" + exception.Message);
        }

        png ??= ReadTextureThroughGpu(texture, width, height, target, source);
        if (png is not { Length: > 0 } || png.Length > MaxPreviewBytes)
        {
            Logger.Debug(
                "Replay",
                "官谱纹理无 native 指针且 GPU 读回失败: id=" + target
                + ", source=" + source
                + ", texture=0x" + texture.ToString("X"));
            return false;
        }

        // OpenGL 上传必须留到 TryGetTexture 的 ImGui 上下文执行。
        _officialPngData = png;
        Logger.Info(
            "Replay",
            "已编码官谱纹理，等待 ImGui 上传: id=" + target + ", source=" + source);
        return true;
    }

    private byte[]? ReadTextureThroughGpu(
        nint sourceTexture,
        int sourceWidth,
        int sourceHeight,
        string target,
        string source)
    {
        if (_graphicsBlit == null
            || _renderTextureClass == null
            || _renderTextureConstructor == null
            || _renderTextureCreate == null
            || _texture2DClass == null
            || _textureConstructor == null
            || _textureReadPixels == null
            || _textureApply == null
            || sourceTexture == 0
            || sourceWidth <= 0
            || sourceHeight <= 0)
        {
            Logger.Debug(
                "Replay",
                "官谱纹理 GPU 读回接口不完整: id=" + target
                + ", source=" + source
                + ", blit=" + (_graphicsBlit != null)
                + ", renderTextureCtor=" + (_renderTextureConstructor != null)
                + ", create=" + (_renderTextureCreate != null)
                + ", readPixels=" + (_textureReadPixels != null)
                + ", apply=" + (_textureApply != null));
            return null;
        }

        int width = Math.Clamp(sourceWidth, 1, 4096);
        int height = Math.Clamp(sourceHeight, 1, 4096);
        nint renderTexture = 0;
        nint renderTextureHandle = 0;
        nint readableTexture = 0;
        nint readableTextureHandle = 0;
        nint previousActive = 0;
        bool activeChanged = false;
        try
        {
            renderTexture = _renderTextureClass.New();
            if (renderTexture == 0)
                return null;
            renderTextureHandle = NewGcHandle(renderTexture);
            if (renderTextureHandle == 0)
                return null;

            int depth = 0;
            int format = 0; // RenderTextureFormat.ARGB32
            nint[] renderTextureArguments = _renderTextureConstructor.ParamCount == 4
                ? new[] { (nint)(&width), (nint)(&height), (nint)(&depth), (nint)(&format) }
                : new[] { (nint)(&width), (nint)(&height), (nint)(&depth) };
            _renderTextureConstructor.Invoke(renderTexture, renderTextureArguments);
            _renderTextureCreate.Invoke(renderTexture);

            if (_renderTextureActiveGetter != null)
                previousActive = _renderTextureActiveGetter.InvokeStatic();
            if (_renderTextureActiveSetter == null)
                return null;
            _renderTextureActiveSetter.InvokeStatic(new[] { renderTexture });
            activeChanged = true;

            _graphicsBlit.InvokeStatic(new[] { sourceTexture, renderTexture });

            readableTexture = _texture2DClass.New();
            if (readableTexture == 0)
                return null;
            readableTextureHandle = NewGcHandle(readableTexture);
            if (readableTextureHandle == 0)
                return null;

            int textureFormat = 5; // TextureFormat.ARGB32
            byte mipChain = 0;
            _textureConstructor.Invoke(readableTexture, new[]
            {
                (nint)(&width),
                (nint)(&height),
                (nint)(&textureFormat),
                (nint)(&mipChain),
            });

            NativeRect rect = new(0f, 0f, width, height);
            int destinationX = 0;
            int destinationY = 0;
            byte recalculateMipMaps = 0;
            _textureReadPixels.Invoke(readableTexture, new[]
            {
                (nint)(&rect),
                (nint)(&destinationX),
                (nint)(&destinationY),
                (nint)(&recalculateMipMaps),
            });

            if (!ApplyReadableTexture(readableTexture))
                return null;
            nint encoded = _encodeToPng?.InvokeStatic(new[] { readableTexture }) ?? 0;
            byte[]? png = ReadByteArray(encoded);
            if (png is { Length: > 0 })
            {
                Logger.Info(
                    "Replay",
                    "已通过 GPU 读回官谱纹理: id=" + target
                    + ", source=" + source
                    + ", size=" + width + "x" + height);
            }
            return png;
        }
        catch (Exception exception)
        {
            Logger.Debug(
                "Replay",
                "官谱纹理 GPU 读回异常: id=" + target
                + ", source=" + source
                + ", reason=" + exception.Message);
            return null;
        }
        finally
        {
            if (activeChanged && _renderTextureActiveSetter != null)
            {
                try { _renderTextureActiveSetter.InvokeStatic(new[] { previousActive }); }
                catch { }
            }
            try { _renderTextureRelease?.Invoke(renderTexture); }
            catch { }
            FreeGcHandle(readableTextureHandle);
            DestroyUnityObject(readableTexture);
            FreeGcHandle(renderTextureHandle);
            DestroyUnityObject(renderTexture);
        }
    }

    private bool ApplyReadableTexture(nint texture)
    {
        if (_textureApply == null || texture == 0)
            return false;
        try
        {
            if (_textureApply.ParamCount >= 2)
            {
                byte updateMipMaps = 0;
                byte makeNoLongerReadable = 0;
                _textureApply.Invoke(texture, new[]
                {
                    (nint)(&updateMipMaps),
                    (nint)(&makeNoLongerReadable),
                });
            }
            else if (_textureApply.ParamCount == 1)
            {
                byte updateMipMaps = 0;
                _textureApply.Invoke(texture, new[] { (nint)(&updateMipMaps) });
            }
            else
            {
                _textureApply.Invoke(texture);
            }
            return true;
        }
        catch (Exception exception)
        {
            Logger.Debug("Replay", "可读官谱纹理 Apply 失败: " + exception.Message);
            return false;
        }
    }

    private static bool OfficialIdsEqualIgnoringSpeedTrial(string left, string right)
    {
        string normalizedLeft = NormalizeOfficialId(left);
        string normalizedRight = NormalizeOfficialId(right);
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalizedLeft.EndsWith("-X", StringComparison.OrdinalIgnoreCase))
            normalizedLeft = normalizedLeft[..^2];
        if (normalizedRight.EndsWith("-X", StringComparison.OrdinalIgnoreCase))
            normalizedRight = normalizedRight[..^2];
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOfficialId(string value)
    {
        string normalized = value?.Trim().Replace('\\', '/') ?? "";
        int slash = normalized.LastIndexOf('/');
        if (slash >= 0)
            normalized = normalized[(slash + 1)..];
        if (normalized.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^7];
        return normalized;
    }

    internal void Clear()
    {
        ReleaseTexture();
        _officialPngData = null;
        _previewPath = "";
        _loadFailed = false;
    }

    internal bool TryGetTexture(out nint textureId, out int width, out int height)
    {
        textureId = 0;
        width = 0;
        height = 0;
        if (_officialPngData is { Length: > 0 })
        {
            IntPtr context = GetCurrentImGuiContext();
            if (context != IntPtr.Zero
                && ReplayOpenGlTexture.TryLoadPng(
                    _officialPngData,
                    out textureId,
                    out width,
                    out height))
            {
                _texture = 0;
                _textureHandle = 0;
                _textureId = textureId;
                _width = width;
                _height = height;
                _isOpenGlTexture = true;
                _textureContext = context;
                _borrowedTexture = false;
                _officialPngData = null;
                return true;
            }
            _officialPngData = null;
            _loadFailed = true;
            return false;
        }
        if (!HasSource || _loadFailed)
            return false;
        if (_textureId != 0 && (_isOpenGlTexture || IsUnityObjectAlive(_texture)))
        {
            textureId = _textureId;
            width = _width;
            height = _height;
            return true;
        }

        ReleaseTexture();
        if (!CanRenderNativeTexture())
            return false;

        NativeImage? image = LoadUnityImageAsset(_previewPath)
            ?? LoadOpenGlImageAsset(_previewPath);
        if (image == null)
        {
            _loadFailed = true;
            Logger.Debug("Replay", $"谱面封面加载失败: {_previewPath}");
            return false;
        }

        _texture = image.Texture;
        _textureHandle = image.TextureHandle;
        _textureId = image.TextureId;
        _width = image.Width;
        _height = image.Height;
        _isOpenGlTexture = image.IsOpenGlTexture;
        _textureContext = image.TextureContext;
        textureId = _textureId;
        width = _width;
        height = _height;
        return true;
    }

    public void Dispose() => Clear();

    private bool CanRenderNativeTexture()
    {
        if (_getGraphicsDeviceType == null)
            return false;
        try
        {
            int graphicsDevice = _getGraphicsDeviceType.InvokeStaticUnbox<int>();
            return graphicsDevice is 8 or 11;
        }
        catch
        {
            return false;
        }
    }

    private NativeImage? LoadUnityImageAsset(string path)
    {
        if (_texture2DClass == null || _textureConstructor == null
            || (_loadImage == null && _loadImageWithReadableFlag == null)
            || _byteClass == null || _textureWidth == null
            || _textureHeight == null || _getNativeTexturePtr == null)
        {
            return null;
        }

        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length <= 0 || file.Length > MaxPreviewBytes)
                return null;
        }
        catch
        {
            return null;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return null; }

        nint texture = 0;
        nint textureHandle = 0;
        try
        {
            texture = _texture2DClass.New();
            if (texture == 0)
                return null;
            textureHandle = NewGcHandle(texture);
            if (textureHandle == 0)
                return null;

            int width = 2;
            int height = 2;
            int format = 5; // TextureFormat.ARGB32
            byte mipChain = 0;
            _textureConstructor.Invoke(texture, new[]
            {
                (nint)(&width),
                (nint)(&height),
                (nint)(&format),
                (nint)(&mipChain),
            });

            nint data = NewByteArray(bytes);
            if (data == 0 || !TryLoadImage(texture, data))
                return null;
            SetTextureEnum(_texture2DClass, texture, "set_wrapMode", 1);
            SetTextureEnum(_texture2DClass, texture, "set_filterMode", 1);

            int loadedWidth = _textureWidth.InvokeUnbox<int>(texture);
            int loadedHeight = _textureHeight.InvokeUnbox<int>(texture);
            nint nativeTexture = _getNativeTexturePtr.InvokeUnbox<nint>(texture);
            if (loadedWidth <= 0 || loadedHeight <= 0 || nativeTexture == 0)
                return null;

            NativeImage result = new()
            {
                Texture = texture,
                TextureHandle = textureHandle,
                TextureId = nativeTexture,
                Width = loadedWidth,
                Height = loadedHeight,
            };
            texture = 0;
            textureHandle = 0;
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            FreeGcHandle(textureHandle);
            DestroyUnityObject(texture);
        }
    }

    private bool TryLoadImage(nint texture, nint data)
    {
        if (_loadImage != null)
        {
            try
            {
                if (_loadImage.InvokeStaticUnbox<byte>(new[] { texture, data }) != 0)
                    return true;
            }
            catch
            {
            }
        }

        if (_loadImageWithReadableFlag == null)
            return false;
        try
        {
            byte markNonReadable = 0;
            return _loadImageWithReadableFlag.InvokeStaticUnbox<byte>(new[]
            {
                texture,
                data,
                (nint)(&markNonReadable),
            }) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void ReleaseTexture()
    {
        nint texture = _texture;
        nint handle = _textureHandle;
        nint textureId = _textureId;
        bool isOpenGlTexture = _isOpenGlTexture;
        IntPtr textureContext = _textureContext;
        _texture = 0;
        _textureHandle = 0;
        _textureId = 0;
        _width = 0;
        _height = 0;
        _isOpenGlTexture = false;
        _textureContext = IntPtr.Zero;
        bool borrowedTexture = _borrowedTexture;
        _borrowedTexture = false;
        if (isOpenGlTexture)
        {
            IntPtr currentContext = GetCurrentImGuiContext();
            if (textureContext != IntPtr.Zero && textureContext == currentContext)
                ReplayOpenGlTexture.Delete(textureId);
            return;
        }
        if (!borrowedTexture)
        {
            FreeGcHandle(handle);
            DestroyUnityObject(texture);
        }
    }

    private static NativeImage? LoadOpenGlImageAsset(string path)
    {
        IntPtr context = GetCurrentImGuiContext();
        if (context == IntPtr.Zero
            || !ReplayOpenGlTexture.TryLoadImage(
                path,
                out nint textureId,
                out int width,
                out int height))
        {
            return null;
        }
        return new NativeImage
        {
            TextureId = textureId,
            Width = width,
            Height = height,
            IsOpenGlTexture = true,
            TextureContext = context,
        };
    }

    private bool IsUnityObjectAlive(nint objectPointer)
    {
        if (objectPointer == 0)
            return false;
        if (_unityObjectImplicit == null)
            return true;
        try
        {
            return _unityObjectImplicit.InvokeStaticUnbox<byte>(new[] { objectPointer }) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static nint InvokeInstanceObject(IRuntimeMethod? method, nint instance)
    {
        if (method == null || instance == 0)
            return 0;
        try { return method.Invoke(instance); }
        catch { return 0; }
    }

    private static string InvokeInstanceString(IRuntimeMethod? method, nint instance)
    {
        nint value = InvokeInstanceObject(method, instance);
        if (value == 0)
            return "";
        try { return new RuntimeString(value).ToString(); }
        catch { return ""; }
    }

    private static T InvokeInstanceUnbox<T>(IRuntimeMethod? method, nint instance, T fallback = default)
        where T : unmanaged
    {
        if (method == null || instance == 0)
            return fallback;
        try { return method.InvokeUnbox<T>(instance); }
        catch { return fallback; }
    }

    private static int InvokeInstanceUnbox(
        IRuntimeMethod? method,
        nint instance,
        nint argument,
        int fallback)
    {
        if (method == null || instance == 0)
            return fallback;
        try { return method.InvokeUnbox<int>(instance, new[] { argument }); }
        catch { return fallback; }
    }

    private static T Read<T>(IRuntimeField? field, nint instance, T fallback)
        where T : unmanaged
    {
        if (field == null || (!field.IsStatic && instance == 0))
            return fallback;
        try { return field.GetValue<T>(instance); }
        catch { return fallback; }
    }

    private static string ReadString(IRuntimeField? field, nint instance)
    {
        nint value = Read(field, instance, nint.Zero);
        return ReadRuntimeString(value);
    }

    private static string ReadRuntimeString(nint value)
    {
        if (value == 0)
            return "";
        try { return new RuntimeString(value).ToString(); }
        catch { return ""; }
    }

    private static byte[]? ReadByteArray(nint array)
    {
        if (array == 0)
            return null;
        try
        {
            RuntimeArray values = new(array);
            if (!values.IsValid || values.Length <= 0 || values.Length > 16 * 1024 * 1024)
                return null;
            byte[] bytes = new byte[values.Length];
            if (RuntimeManager.IsIl2Cpp)
            {
                nint data = values.DataPtr;
                if (data == 0)
                    return null;
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return bytes;
            }
            if (!RuntimeManager.IsMono)
                return null;
            for (nuint index = 0; index < (nuint)bytes.Length; index++)
            {
                nint data = MonoFunctions.MonoArrayAddrWithSize(values.Ptr, sizeof(byte), index);
                if (data == 0)
                    return null;
                bytes[index] = *(byte*)data;
            }
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static int GetArrayLength(nint array)
    {
        if (array == 0)
            return 0;
        try
        {
            RuntimeArray values = new(array);
            return values.IsValid ? values.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private void DestroyUnityObject(nint objectPointer)
    {
        if (objectPointer == 0 || _destroyObject == null)
            return;
        try { _destroyObject.InvokeStatic(new[] { objectPointer }); }
        catch { }
    }

    private nint NewByteArray(byte[] bytes)
    {
        RuntimeArray array = RuntimeArray.New(_domain, _byteClass!.Ptr, bytes.Length);
        if (!array.IsValid)
            return 0;
        if (RuntimeManager.IsIl2Cpp)
        {
            nint data = array.DataPtr;
            if (data == 0)
                return 0;
            Marshal.Copy(bytes, 0, data, bytes.Length);
            return array.Ptr;
        }
        if (!RuntimeManager.IsMono)
            return 0;
        for (nuint index = 0; index < (nuint)bytes.Length; index++)
        {
            nint data = MonoFunctions.MonoArrayAddrWithSize(array.Ptr, sizeof(byte), index);
            if (data == 0)
                return 0;
            *(byte*)data = bytes[index];
        }
        return array.Ptr;
    }

    private static nint NewGcHandle(nint objectPointer)
    {
        if (objectPointer == 0)
            return 0;
        if (RuntimeManager.IsIl2Cpp)
            return Il2CppFunctions.il2cpp_gchandle_new(objectPointer, false);
        if (RuntimeManager.IsMono)
            return (nint)MonoFunctions.MonoGCHandleNew(objectPointer, false);
        return 0;
    }

    private static void FreeGcHandle(nint handle)
    {
        if (handle == 0)
            return;
        if (RuntimeManager.IsIl2Cpp)
            Il2CppFunctions.il2cpp_gchandle_free(handle);
        else if (RuntimeManager.IsMono)
            MonoFunctions.MonoGCHandleFree((uint)handle);
    }

    private static void SetTextureEnum(IRuntimeClass textureClass, nint texture, string setterName, int value)
    {
        IRuntimeMethod? setter = textureClass.GetMethod(setterName, 1);
        if (setter == null)
            return;
        try { setter.Invoke(texture, new[] { (nint)(&value) }); }
        catch { }
    }

    private static IntPtr GetCurrentImGuiContext()
    {
        try { return ImGui.GetCurrentContext(); }
        catch { return IntPtr.Zero; }
    }

    private nint GetRuntimeTypeObject(IRuntimeClass? runtimeClass)
    {
        if (runtimeClass == null)
            return 0;
        if (RuntimeManager.IsIl2Cpp)
        {
            nint type = Il2CppFunctions.il2cpp_class_get_type(runtimeClass.Ptr);
            return type == 0 ? 0 : Il2CppFunctions.il2cpp_type_get_object(type);
        }
        if (RuntimeManager.IsMono)
        {
            nint type = MonoFunctions.MonoClassGetType(runtimeClass.Ptr);
            return type == 0
                ? 0
                : (nint)Methods.mono_type_get_object(
                    (_MonoDomain*)_domain.Ptr,
                    (_MonoType*)type);
        }
        return 0;
    }

    private static IRuntimeField? FindField(IRuntimeClass? type, params string[] names)
    {
        if (type == null)
            return null;
        foreach (string name in names)
        {
            IRuntimeField? field = type.GetField(name);
            if (field != null)
                return field;
        }
        return null;
    }

    private static IRuntimeClass? FindClassInDomain(IAppDomain domain, string namespaze, string name)
    {
        foreach (IRuntimeAssembly assembly in domain.GetAssemblies())
        {
            try
            {
                IRuntimeClass? type = assembly.GetClass(namespaze, name);
                if (type != null)
                    return type;
            }
            catch
            {
            }
        }
        return null;
    }

    private sealed class NativeImage
    {
        internal nint Texture;
        internal nint TextureHandle;
        internal nint TextureId;
        internal int Width;
        internal int Height;
        internal bool IsOpenGlTexture;
        internal IntPtr TextureContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(float x, float y, float width, float height)
    {
        internal float X = x;
        internal float Y = y;
        internal float Width = width;
        internal float Height = height;
    }
}
