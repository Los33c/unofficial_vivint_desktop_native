set -e
SRC=$HOME/mpvbuild/src; mkdir -p $SRC; cd $SRC
g() { [ -d "$3" ] || git clone -q --depth 1 --branch "$2" ${4:-} "$1" "$3"; echo "ok $3"; }
g https://git.ffmpeg.org/ffmpeg.git n9.0.2 ffmpeg
g https://github.com/mpv-player/mpv.git v0.42.0 mpv
g https://code.videolan.org/videolan/libplacebo.git v7.387.0 libplacebo "--recurse-submodules --shallow-submodules"
g https://github.com/libass/libass.git 0.17.5 libass
g https://gitlab.freedesktop.org/freetype/freetype.git VER-2-15-0 freetype
g https://github.com/fribidi/fribidi.git v1.0.18 fribidi
g https://github.com/harfbuzz/harfbuzz.git 15.1.0 harfbuzz
g https://github.com/google/shaderc.git v2026.4 shaderc
g https://github.com/KhronosGroup/SPIRV-Cross.git vulkan-sdk-1.4.363.0 SPIRV-Cross
g https://github.com/madler/zlib.git v1.4 zlib
cat shaderc/DEPS | grep -E "_revision|_git" 
du -sh $SRC
