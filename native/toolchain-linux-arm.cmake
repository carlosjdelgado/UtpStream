# CMake toolchain file for cross-compiling libutp to ARMv7 hard-float Linux
# (Raspberry Pi 2/3/4/Zero 2 W with a 32-bit OS, plus any other linux-arm
# device). Used by the CI native build job; you can also point to it
# locally:
#
#   cmake -S native -B build-arm \
#       -DCMAKE_TOOLCHAIN_FILE=$PWD/native/toolchain-linux-arm.cmake \
#       -DCMAKE_BUILD_TYPE=Release
#   cmake --build build-arm -j

set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR arm)

set(CMAKE_C_COMPILER   arm-linux-gnueabihf-gcc)
set(CMAKE_CXX_COMPILER arm-linux-gnueabihf-g++)

# Don't accidentally pick up host headers/libraries.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
