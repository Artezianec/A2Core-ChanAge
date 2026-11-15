cmake_minimum_required(VERSION 3.20)
project(A2Core.Redirect LANGUAGES C CXX)

add_library(A2Core.Redirect SHARED hooks.cpp redirect_map.cpp trace.cpp)
set_target_properties(A2Core.Redirect PROPERTIES OUTPUT_NAME "A2Core.Redirect" CXX_STANDARD 17 PREFIX "" SUFFIX ".dll")
target_compile_definitions(A2Core.Redirect PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(A2Core.Redirect PRIVATE ws2_32)

if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
  # Avoid a runtime dependency on libgcc/libstdc++ (and pthread) when built with mingw.
  target_link_options(A2Core.Redirect PRIVATE -static-libgcc -static-libstdc++ -static)
endif()

include(FetchContent)
FetchContent_Declare(
  minhook
  GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
  GIT_TAG v1.3.3
)
FetchContent_GetProperties(minhook)
if(NOT minhook_POPULATED)
  FetchContent_Populate(minhook)
endif()

add_library(minhook STATIC
  ${minhook_SOURCE_DIR}/src/buffer.c
  ${minhook_SOURCE_DIR}/src/hook.c
  ${minhook_SOURCE_DIR}/src/trampoline.c
  ${minhook_SOURCE_DIR}/src/hde/hde64.c
)
target_include_directories(minhook PUBLIC ${minhook_SOURCE_DIR}/include)
target_compile_definitions(minhook PRIVATE WIN32_LEAN_AND_MEAN)

target_link_libraries(A2Core.Redirect PRIVATE minhook)
target_include_directories(A2Core.Redirect PRIVATE ${minhook_SOURCE_DIR}/include)

if(MSVC)
  set_property(TARGET minhook PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  set_property(TARGET A2Core.Redirect PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
