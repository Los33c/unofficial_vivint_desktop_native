set -e
cd $HOME/mpvbuild/src/shaderc/third_party
f() { [ -d "$1/.git" ] || { mkdir -p $1; cd $1; git init -q; git remote add origin $2; git fetch -q --depth 1 origin $3; git -c advice.detachedHead=false checkout -q FETCH_HEAD; cd ..; }; echo "ok $1"; }
f glslang https://github.com/KhronosGroup/glslang.git e1b562a8bed273a02f30b59b66a5d499793cede5
f spirv-tools https://github.com/KhronosGroup/SPIRV-Tools.git ef96ed763b43b59b33b31b362f09a02b729fa1c9
f spirv-headers https://github.com/KhronosGroup/SPIRV-Headers.git 04fd3caa1e8267e4d95c806cad901181728e1006
cd $HOME/mpvbuild/src
echo "== libass"; grep -E "^option\(" libass/meson_options.txt 2>/dev/null | sed -E "s/option\('([^']+)'.*/\1/" | tr '\n' ' '; ls libass/meson.build 2>/dev/null
echo; echo "== placebo"; sed -E -n "s/^option\('([^']+)'.*/\1/p" libplacebo/meson_options.txt | tr '\n' ' '
echo; echo "== mpv"; sed -E -n "s/^option\('([^']+)'.*/\1/p" mpv/meson.options mpv/meson_options.txt 2>/dev/null | tr '\n' ' '
echo; grep -n -E "shaderc|spirv-cross|spirv_cross" mpv/meson.build | head -20
grep -n -E "dependency\('(shaderc|spirv)" libplacebo/src/meson.build libplacebo/src/*/meson.build | head
echo "== harfbuzz"; sed -E -n "s/^option\('([^']+)'.*/\1/p" harfbuzz/meson_options.txt harfbuzz/meson.options 2>/dev/null | tr '\n' ' '
echo; echo "== zlib"; grep -E "^option\(" zlib/CMakeLists.txt; ls zlib/win32
