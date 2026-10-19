#pragma once
// Minimal, layout-exact subset of xNVSE's PluginAPI.h (https://github.com/xNVSE/NVSE, nvse/PluginAPI.h).
// Only the parts this plugin uses are declared, so the plugin builds without the whole xNVSE tree.
// Field order and types MUST match PluginAPI.h; do not reorder.

#include <cstdint>

using UInt8 = std::uint8_t;
using UInt16 = std::uint16_t;
using UInt32 = std::uint32_t;
using SInt32 = std::int32_t;

class TESObjectREFR;
struct CommandInfo;

typedef UInt32 PluginHandle;
constexpr PluginHandle kPluginHandle_Invalid = 0xFFFFFFFF;

enum
{
    kInterface_Serialization = 0,
    kInterface_Console,
    kInterface_Messaging,
    kInterface_CommandTable,
    kInterface_StringVar,
    kInterface_ArrayVar,
    kInterface_Script,
    kInterface_Data,
    kInterface_EventManager,
    kInterface_Logging,
    kInterface_PlayerControls,
};

// nvse_version.h
#define MAKE_NEW_VEGAS_VERSION_EX(major, minor, build, sub) (((major & 0xFF) << 24) | ((minor & 0xFF) << 16) | ((build & 0xFFF) << 4) | (sub & 0xF))
#define MAKE_NEW_VEGAS_VERSION(major, minor, build) MAKE_NEW_VEGAS_VERSION_EX(major, minor, build, 0)
constexpr UInt32 RUNTIME_VERSION_1_4_0_525 = MAKE_NEW_VEGAS_VERSION(4, 0, 525); // 0x040020D0
// The plugin's event-manager usage needs xNVSE 6.3.0 or newer.
constexpr UInt32 MIN_NVSE_VERSION = MAKE_NEW_VEGAS_VERSION(6, 3, 0);

struct NVSEInterface
{
    UInt32 nvseVersion;
    UInt32 runtimeVersion;
    UInt32 editorVersion;
    UInt32 isEditor;
    bool (*RegisterCommand)(CommandInfo* info);
    void (*SetOpcodeBase)(UInt32 opcode);
    void* (*QueryInterface)(UInt32 id);
    PluginHandle (*GetPluginHandle)(void);
    bool (*RegisterTypedCommand)(CommandInfo* info, UInt32 retnType);
    const char* (*GetRuntimeDirectory)();
    UInt32 isNogore;
};

struct NVSEMessagingInterface
{
    struct Message
    {
        const char* sender;
        UInt32 type;
        UInt32 dataLen;
        void* data;
    };

    typedef void (*EventCallback)(Message* msg);

    enum
    {
        kMessage_PostLoad = 0,
        kMessage_ExitGame,
        kMessage_ExitToMainMenu,
        kMessage_LoadGame,
        kMessage_SaveGame,
        kMessage_ScriptPrecompile,
        kMessage_PreLoadGame,
        kMessage_ExitGame_Console,
        kMessage_PostLoadGame,
        kMessage_PostPostLoad,
        kMessage_RuntimeScriptError,
        kMessage_DeleteGame,
        kMessage_RenameGame,
        kMessage_RenameNewGame,
        kMessage_NewGame,
        kMessage_DeleteGameName,
        kMessage_RenameGameName,
        kMessage_RenameNewGameName,
        kMessage_DeferredInit,
        kMessage_ClearScriptDataCache,
        kMessage_MainGameLoop,
    };

    UInt32 version;
    bool (*RegisterListener)(PluginHandle listener, const char* sender, EventCallback handler);
    bool (*Dispatch)(PluginHandle sender, UInt32 messageType, void* data, UInt32 dataLen, const char* receiver);
};

struct NVSEEventManagerInterface
{
    typedef void (*NativeEventHandler)(TESObjectREFR* thisObj, void* parameters);

    enum ParamType : UInt8
    {
        eParamType_Float = 0,
        eParamType_Int,
        eParamType_String,
        eParamType_Array,
        eParamType_RefVar,
        eParamType_Reference,
        eParamType_BaseForm,
        eParamType_Invalid,
        eParamType_Anything,
    };

    enum EventFlags : UInt32
    {
        kFlags_None = 0,
        kFlag_FlushOnLoad = 1 << 0,
        kFlag_HasUnknownArgTypes = 1 << 1,
        kFlag_AllowScriptDispatch = 1 << 2,
    };

    bool (*RegisterEvent)(const char* name, UInt8 numParams, ParamType* paramTypes, EventFlags flags);
    bool (*DispatchEvent)(const char* eventName, TESObjectREFR* thisObj, ...);
    void* DispatchEventAlt; // not used (signature intentionally omitted)
    bool (*SetNativeEventHandler)(const char* eventName, NativeEventHandler func);
    bool (*RemoveNativeEventHandler)(const char* eventName, NativeEventHandler func);
};

struct PluginInfo
{
    enum
    {
        kInfoVersion = 1
    };

    UInt32 infoVersion;
    const char* name;
    UInt32 version;
};
