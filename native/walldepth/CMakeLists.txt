# libwalldepth.so: QNN HTP depth inference for the Unity app (arm64-v8a only).
# Configure through build-android.ps1, which supplies Unity's NDK toolchain and QNN_SDK_ROOT.
cmake_minimum_required(VERSION 3.22)
project(walldepth CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT QNN_SDK_ROOT)
  message(FATAL_ERROR "Pass -DQNN_SDK_ROOT=<QAIRT SDK root> (see Task 7, Step 1)")
endif()
set(QNN_INCLUDE "${QNN_SDK_ROOT}/include/QNN")
if(NOT EXISTS "${QNN_INCLUDE}/QnnInterface.h")
  message(FATAL_ERROR "QnnInterface.h not found under ${QNN_INCLUDE}")
endif()

# The QNN libraries are dlopen()ed at runtime, so nothing links against them here: the APK
# carries the exact versions pinned in tools/models.lock.json.
add_library(walldepth SHARED src/walldepth.cpp)
target_include_directories(walldepth PUBLIC include PRIVATE src ${QNN_INCLUDE})
target_compile_options(walldepth PRIVATE -Wall -Wextra -O2 -fvisibility=hidden)
target_link_libraries(walldepth PRIVATE log dl)
# 16 KB-aligned segments: required by Google Play for apps targeting Android 15+.
target_link_options(walldepth PRIVATE -Wl,-z,max-page-size=16384 -Wl,--gc-sections)

add_executable(wd_convert_test test/convert_test.cpp)
target_include_directories(wd_convert_test PRIVATE src)

add_executable(wd_selftest test/selftest.cpp)
target_link_libraries(wd_selftest PRIVATE walldepth)
