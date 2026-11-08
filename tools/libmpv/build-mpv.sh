#!/bin/bash
# Minimal libmpv-2.dll for Vivint Desktop: RTSP/HTTPS, H.264/H.265 + Opus/AAC, d3d11 gpu-next, d3d11va.
set -euo pipefail
R=$HOME/mpvbuild; S=$R/src; P=$R/prefix; B=$R/build
mkdir -p $P/lib/pkgconfig $B
H=x86_64-w64-mingw32
export PKG_CONFIG_LIBDIR=$P/lib/pkgconfig; unset PKG_CONFIG_PATH || true
OPT="-O2 -ffunction-sections -fdata-sections"
J=3
STDCXX=$($H-g++-posix -print-file-name=libstdc++.a)

cat > $R/cross.txt <<EOF
[binaries]
c = '$H-gcc-posix'
cpp = '$H-g++-posix'
ar = '$H-ar'
strip = '$H-strip'
windres = '$H-windres'
dlltool = '$H-dlltool'
pkg-config = 'pkg-config'
nasm = 'nasm'
[properties]
pkg_config_libdir = ['$P/lib/pkgconfig']
[built-in options]
c_args = ['-O2', '-ffunction-sections', '-fdata-sections']
cpp_args = ['-O2', '-ffunction-sections', '-fdata-sections']
[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF

cat > $R/tc.cmake <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR x86_64)
set(CMAKE_C_COMPILER $H-gcc-posix)
set(CMAKE_CXX_COMPILER $H-g++-posix)
set(CMAKE_RC_COMPILER $H-windres)
set(CMAKE_FIND_ROOT_PATH /usr/$H $P)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
EOF

step() { echo; echo "######## $1  $(date +%T)"; }
mp() { # meson project: name [args...]
  local n=$1; shift; [ -f $B/$n.done ] && { echo "skip $n"; return; }
  step $n; rm -rf $B/$n
  meson setup $B/$n $S/$n --cross-file $R/cross.txt --prefix=$P --libdir=lib \
    --default-library=static --buildtype=release -Doptimization=2 -Db_ndebug=true "$@"
  ninja -C $B/$n -j$J install; touch $B/$n.done; }
cm() { # cmake project: name srcdir [args...]
  local n=$1 src=$2; shift 2; [ -f $B/$n.done ] && { echo "skip $n"; return; }
  step $n; rm -rf $B/$n
  cmake -G Ninja -S $src -B $B/$n -DCMAKE_TOOLCHAIN_FILE=$R/tc.cmake -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX=$P -DCMAKE_INSTALL_LIBDIR=lib -DBUILD_SHARED_LIBS=OFF \
    -DCMAKE_C_FLAGS_RELEASE="$OPT -DNDEBUG" -DCMAKE_CXX_FLAGS_RELEASE="$OPT -DNDEBUG" "$@"
  ninja -C $B/$n -j$J install; touch $B/$n.done; }

# ---- zlib (PNG snapshots)
cm zlib $S/zlib -DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_TESTING=OFF
[ -f $P/lib/libz.a ] || cp "$(ls $P/lib/lib*z*.a | head -1)" $P/lib/libz.a
cat > $P/lib/pkgconfig/zlib.pc <<EOF
prefix=$P
Name: zlib
Description: zlib
Version: 1.4
Libs: -L\${prefix}/lib -lz
Cflags: -I\${prefix}/include
EOF

# ---- libass and its deps (mpv requires libass for OSD text)
mp freetype -Dbrotli=disabled -Dbzip2=disabled -Dharfbuzz=disabled -Dpng=disabled -Dzlib=disabled -Dtests=disabled
mp fribidi -Ddocs=false -Dbin=false -Dtests=false
mp harfbuzz -Dglib=disabled -Dgobject=disabled -Dcairo=disabled -Dchafa=disabled -Dicu=disabled \
  -Dgraphite2=disabled -Dfreetype=disabled -Dfontations=disabled -Dgdi=disabled -Ddirectwrite=disabled \
  -Dharfrust=disabled -Dkbts=disabled -Dwasm=disabled -Draster=disabled -Dvector=disabled -Dgpu=disabled \
  -Dsubset=disabled -Dpng=disabled -Dzlib=disabled -Dtests=disabled -Dintrospection=disabled \
  -Ddocs=disabled -Dutilities=disabled -Dbenchmark=disabled
mp libass -Dfontconfig=disabled -Ddirectwrite=enabled -Dcoretext=disabled -Dasm=enabled \
  -Dlibunibreak=disabled -Dtest=disabled -Dcompare=disabled -Dprofile=disabled -Dfuzz=disabled -Dcheckasm=disabled

# ---- FFmpeg: only what the app uses
if [ ! -f $B/ffmpeg.done ]; then
  step ffmpeg; rm -rf $B/ffmpeg; mkdir -p $B/ffmpeg; cd $B/ffmpeg
  $S/ffmpeg/configure --prefix=$P --arch=x86_64 --target-os=mingw32 --enable-cross-compile \
    --cross-prefix=$H- --cc=$H-gcc-posix --cxx=$H-g++-posix --pkg-config=pkg-config \
    --extra-cflags="$OPT" --enable-static --disable-shared \
    --disable-everything --disable-autodetect --disable-programs --disable-doc --disable-debug \
    --disable-avdevice \
    --enable-w32threads --enable-network --enable-schannel --enable-zlib --enable-d3d11va --enable-dxva2 \
    --enable-avcodec --enable-avformat --enable-avfilter --enable-swscale --enable-swresample \
    --enable-protocol=file,pipe,data,tcp,udp,rtp,http,https,httpproxy,tls,crypto \
    --enable-demuxer=rtsp,sdp,rtp,mov,hls,mpegts,aac,matroska,ogg \
    --enable-decoder=h264,hevc,aac,aac_latm,opus,pcm_mulaw,pcm_alaw,pcm_s16le,pcm_s16be \
    --enable-parser=h264,hevc,aac,aac_latm,opus \
    --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb,extract_extradata,null \
    --enable-hwaccel=h264_d3d11va,h264_d3d11va2,hevc_d3d11va,hevc_d3d11va2,h264_dxva2,hevc_dxva2 \
    --enable-encoder=png \
    --enable-filter=abuffer,abuffersink,buffer,buffersink,aformat,format,aresample,anull,null,scale
  make -j$J; make install; touch $B/ffmpeg.done
fi

# ---- shaderc (GLSL -> SPIR-V, needed by mpv and libplacebo for d3d11)
cm shaderc $S/shaderc -DSHADERC_SKIP_TESTS=ON -DSHADERC_SKIP_EXAMPLES=ON -DSHADERC_SKIP_COPYRIGHT_CHECK=ON \
  -DSHADERC_ENABLE_WERROR_COMPILE=OFF -DSPIRV_SKIP_EXECUTABLES=ON -DSPIRV_SKIP_TESTS=ON \
  -DSPIRV_TOOLS_BUILD_STATIC=ON -DENABLE_GLSLANG_BINARIES=OFF -DGLSLANG_TESTS=OFF -DGLSLANG_ENABLE_INSTALL=OFF \
  -DSKIP_GLSLANG_INSTALL=ON -DSKIP_SPIRV_TOOLS_INSTALL=ON -DSPIRV_HEADERS_SKIP_INSTALL=ON -DENABLE_OPT=ON
cat > $P/lib/pkgconfig/shaderc.pc <<EOF
prefix=$P
Name: shaderc
Description: shaderc (static, combined)
Version: 2026.4
Libs: -L\${prefix}/lib -lshaderc_combined $STDCXX
Cflags: -I\${prefix}/include
EOF

# ---- SPIRV-Cross (SPIR-V -> HLSL for d3d11)
cm spirv-cross $S/SPIRV-Cross -DSPIRV_CROSS_SHARED=OFF -DSPIRV_CROSS_STATIC=ON -DSPIRV_CROSS_CLI=OFF \
  -DSPIRV_CROSS_ENABLE_TESTS=OFF -DSPIRV_CROSS_ENABLE_C_API=ON -DSPIRV_CROSS_ENABLE_GLSL=ON \
  -DSPIRV_CROSS_ENABLE_HLSL=ON -DSPIRV_CROSS_ENABLE_MSL=OFF -DSPIRV_CROSS_ENABLE_CPP=OFF \
  -DSPIRV_CROSS_ENABLE_REFLECT=OFF -DSPIRV_CROSS_ENABLE_UTIL=OFF -DSPIRV_CROSS_SKIP_INSTALL=OFF
SVER=$(awk '/SPVC_C_API_VERSION_MAJOR/{a=$3} /SPVC_C_API_VERSION_MINOR/{b=$3} /SPVC_C_API_VERSION_PATCH/{c=$3} END{print a"."b"."c}' $P/include/spirv_cross/spirv_cross_c.h)
cat > $P/lib/pkgconfig/spirv-cross-c-shared.pc <<EOF
prefix=$P
Name: spirv-cross-c-shared
Description: SPIRV-Cross C API (static)
Version: $SVER
Libs: -L\${prefix}/lib -lspirv-cross-c -lspirv-cross-hlsl -lspirv-cross-glsl -lspirv-cross-core $STDCXX
Cflags: -I\${prefix}/include/spirv_cross
EOF

# ---- libplacebo (gpu-next renderer), d3d11 only
mp libplacebo -Dvulkan=disabled -Dopengl=disabled -Dd3d11=enabled -Dglslang=disabled -Dshaderc=enabled \
  -Dlcms=disabled -Dlibdovi=disabled -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false \
  -Dunwind=disabled -Dxxhash=disabled

# ---- mpv as libmpv-2.dll, everything optional off except what the app uses
if [ ! -f $B/mpv.done ]; then
  step mpv; rm -rf $B/mpv
  meson setup $B/mpv $S/mpv --cross-file $R/cross.txt --prefix=$P --libdir=lib \
    --default-library=shared --buildtype=release -Doptimization=2 -Db_ndebug=true \
    -Dc_link_args="-static -Wl,--gc-sections" -Dcpp_link_args="-static -Wl,--gc-sections" \
    --auto-features=disabled -Dlibmpv=true -Dcplayer=false -Dtests=false -Dfuzzers=false \
    -Dmanpage-build=disabled -Dhtml-build=disabled -Dpdf-build=disabled \
    -Dd3d11=enabled -Dshaderc=enabled -Dspirv-cross=enabled -Dd3d-hwaccel=enabled \
    -Dwasapi=enabled -Dvector=enabled -Dwin32-threads=enabled -Dgl=disabled -Dplain-gl=disabled
  ninja -C $B/mpv -j$J; touch $B/mpv.done
fi

step result
DLL=$(ls $B/mpv/libmpv-2.dll)
$H-strip -s -o $R/libmpv-2.dll $DLL
ls -la $R/libmpv-2.dll
echo "imports:"; $H-objdump -p $R/libmpv-2.dll | grep "DLL Name" | sort -u
echo BUILD_OK
