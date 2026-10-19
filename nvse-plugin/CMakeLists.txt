cmake_minimum_required(VERSION 3.21)
project(MojaveDesktopRadio VERSION 0.2.1 LANGUAGES CXX)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "Fallout: New Vegas is a 32-bit process. Configure with:  cmake -A Win32 ...  (or use the presets)")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the DLL does not depend on a particular VC++ redistributable being installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

configure_file(src/version.rc.in ${CMAKE_CURRENT_BINARY_DIR}/version.rc @ONLY)

add_library(MojaveDesktopRadio SHARED
    ${CMAKE_CURRENT_BINARY_DIR}/version.rc
    src/main.cpp
    src/pipe_client.cpp
    src/pipe_client.h
    src/mini_json.cpp
    src/mini_json.h
    src/settings.cpp
    src/settings.h
    src/text.cpp
    src/text.h
    src/log.cpp
    src/log.h
    src/nvse_api.h
)

target_compile_definitions(MojaveDesktopRadio PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS)
# /Brepro: no timestamps in the output, so rebuilding with the same MSVC version gives a byte-identical DLL
target_compile_options(MojaveDesktopRadio PRIVATE /W4 /permissive- /EHsc /utf-8 /Zi /wd4100 /Brepro)
target_link_options(MojaveDesktopRadio PRIVATE /DEBUG /Brepro /PDBALTPATH:MojaveDesktopRadio.pdb $<$<CONFIG:Release>:/OPT:REF /OPT:ICF>)
set_target_properties(MojaveDesktopRadio PROPERTIES OUTPUT_NAME "MojaveDesktopRadio")

# Offline harness: drives the real DLL with a fake xNVSE + fake Pip-Boy radio (see tests/harness.cpp)
option(MDR_BUILD_HARNESS "Build the offline plugin test harness" ON)
if(MDR_BUILD_HARNESS)
    add_executable(mdr_plugin_harness tests/harness.cpp)
    target_compile_options(mdr_plugin_harness PRIVATE /W4 /EHsc /utf-8)
    add_dependencies(mdr_plugin_harness MojaveDesktopRadio)
endif()

# Optional: copy the DLL into a mod folder after each build, e.g.
#   cmake --preset win32 -DMDR_DEPLOY_DIR="<MO2>/mods/Mojave Desktop Radio/NVSE/Plugins"
set(MDR_DEPLOY_DIR "" CACHE PATH "Folder the built DLL is copied to (empty = no copy)")
if(MDR_DEPLOY_DIR)
    add_custom_command(TARGET MojaveDesktopRadio POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${MDR_DEPLOY_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:MojaveDesktopRadio>" "${MDR_DEPLOY_DIR}"
        COMMENT "Deploying MojaveDesktopRadio.dll to ${MDR_DEPLOY_DIR}")
endif()
