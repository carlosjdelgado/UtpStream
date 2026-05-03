cmake_minimum_required(VERSION 3.16)
project(utp LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(LIBUTP_SOURCES
    libutp/utp_internal.cpp
    libutp/utp_utils.cpp
    libutp/utp_hash.cpp
    libutp/utp_callbacks.cpp
    libutp/utp_api.cpp
    libutp/utp_packedsockaddr.cpp
)

if(WIN32)
    list(APPEND LIBUTP_SOURCES libutp/libutp_inet_ntop.cpp)
endif()

add_library(utp SHARED ${LIBUTP_SOURCES})

target_include_directories(utp PUBLIC libutp)

if(WIN32)
    target_compile_definitions(utp PRIVATE WIN32 _CRT_SECURE_NO_WARNINGS _WINSOCK_DEPRECATED_NO_WARNINGS)
    target_link_libraries(utp PRIVATE ws2_32)
    set_target_properties(utp PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)
else()
    target_compile_definitions(utp PRIVATE POSIX)
    target_compile_options(utp PRIVATE -Wall -fno-exceptions -fno-rtti -fpermissive -Wno-sign-compare)
    set_target_properties(utp PROPERTIES C_VISIBILITY_PRESET default
                                         CXX_VISIBILITY_PRESET default)
    include(CheckLibraryExists)
    check_library_exists(rt clock_gettime "" HAVE_LIBRT)
    if(HAVE_LIBRT)
        target_link_libraries(utp PRIVATE rt)
    endif()
endif()

# Output: libutp.so on Linux, libutp.dylib on macOS, utp.dll on Windows
set_target_properties(utp PROPERTIES OUTPUT_NAME utp)

# Reference CLIs (ucat) and the bench binary are POSIX-only and only useful
# on the build host — when we cross-compile (e.g. to linux-arm for Raspberry
# Pi), skip them, since they'd produce binaries we can't run on the CI
# runner anyway.
if(NOT WIN32 AND NOT CMAKE_CROSSCOMPILING)
    # ucat — libutp's reference netcat-style CLI.
    add_executable(ucat libutp/ucat.c)
    target_link_libraries(ucat PRIVATE utp)
    target_include_directories(ucat PRIVATE libutp)
    target_compile_definitions(ucat PRIVATE POSIX)
    target_compile_options(ucat PRIVATE -Wno-format -Wno-pointer-sign)

    # native_bench — minimal libutp throughput benchmark, configured to match
    # UtpStream's defaults so the C and .NET numbers are directly comparable.
    add_executable(native_bench ${CMAKE_SOURCE_DIR}/../bench/native_bench/native_bench.c)
    target_link_libraries(native_bench PRIVATE utp pthread)
    target_include_directories(native_bench PRIVATE libutp)
    target_compile_definitions(native_bench PRIVATE POSIX)
    target_compile_options(native_bench PRIVATE -O2)
endif()
