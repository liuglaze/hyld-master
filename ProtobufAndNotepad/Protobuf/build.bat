@echo off
setlocal

REM ============================================================
REM  权威协议源：本目录下的 SocketProto.proto
REM
REM  运行时产物：
REM    ..\..\Client\Assets\Scripts\Server\SocketProto.cs                       protoc 生成（客户端）
REM    ..\..\Server\Server\SocketProto.cs                                      protoc 生成（服务端）
REM    ..\..\Client\Assets\Scripts\PMNet\Generated\SocketProto.PMNet.g.cs      PMNetGen 生成
REM  工具目录产物：
REM    .\CSharp\**   仅供工具与中间产物使用，不视为运行时权威文件
REM
REM  本脚本职责：
REM    1) 前置检查与 protoc 版本断言，避免换版本后产物出现无意义差异
REM    2) protoc 生成 + 行尾归一 + 生成确定性校验
REM    3) PMNetGen 生成 + 产物与权威 proto 的同步校验
REM
REM  任一步失败都会以非 0 退出码结束，避免「proto 改了但产物没重新生成」
REM  变成静默故障。传入 --check-only 时只校验、不写入任何产物，供提交前或 CI 使用。
REM
REM  两个已实测的坑，改动本脚本时必须同时满足：
REM    A) protoc 会把「proto 的传入路径」写进生成文件头部的 source: 字段。
REM       因此必须在脚本目录下、以裸文件名 SocketProto.proto 调用，
REM       否则生成内容与已提交产物不一致（传绝对路径会写成完整路径）。
REM    B) protoc 写出 LF，而本仓库追踪的文件是 CRLF，内容一致但会让 git 把产物
REM       报成「已修改」。所以生成后统一改写为 CRLF；归一逻辑复用 PMNetGen 的
REM       --normalize-eol，不给本脚本引入 Python / PowerShell 等新依赖。
REM       确定性比对前把临时产物也归一到 CRLF，再用 fc /b 精确比对。
REM
REM  输出信息刻意使用 ASCII，避免 cmd 代码页与文件 UTF-8 编码不一致导致乱码；
REM  中文只保留在不会被解析执行的 REM 注释里。
REM ============================================================

set "SCRIPT_DIR=%~dp0"
set "REPO_ROOT=%SCRIPT_DIR%..\.."

set "PROTO_NAME=SocketProto.proto"
set "PROTO=%SCRIPT_DIR%%PROTO_NAME%"
set "PROTOC=%SCRIPT_DIR%protoc.exe"
set "EXPECTED_PROTOC=libprotoc 3.11.1"

set "OUT_TOOL=%SCRIPT_DIR%CSharp"
set "OUT_CLIENT=%REPO_ROOT%\Client\Assets\Scripts\Server"
set "OUT_SERVER=%REPO_ROOT%\Server\Server"

set "PMNETGEN_PROJ=%REPO_ROOT%\Tools\PMNetGen\PMNetGen.csproj"
set "PMNETGEN_DLL=%REPO_ROOT%\Tools\PMNetGen\bin\Release\net8.0\PMNetGen.dll"
set "OUT_PMNET_DIR=%REPO_ROOT%\Client\Assets\Scripts\PMNet\Generated"
set "OUT_PMNET=%OUT_PMNET_DIR%\SocketProto.PMNet.g.cs"
set "PMNET_PROTO_REL=ProtobufAndNotepad/Protobuf/SocketProto.proto"

set "TMP_DETERMINISM=%TEMP%\hyld_proto_determinism"

set "CHECK_ONLY=0"
if /i "%~1"=="--check-only" set "CHECK_ONLY=1"

echo [build] repo root : %REPO_ROOT%
if "%CHECK_ONLY%"=="1" (
  echo [build] mode      : check-only ^(no files will be written^)
) else (
  echo [build] mode      : generate
)
echo.

REM ---------------- 1) 前置检查 ----------------

if not exist "%PROTO%" (
  echo [build][ERROR] 权威 proto 不存在: %PROTO%
  exit /b 1
)

if not exist "%PROTOC%" (
  echo [build][ERROR] 找不到 protoc: %PROTOC%
  echo [build][ERROR] 本脚本只使用同目录下的 protoc.exe，不依赖 PATH。
  exit /b 1
)

REM ---------------- 2) protoc 版本断言 ----------------

set "PROTOC_VERSION="
for /f "usebackq delims=" %%v in (`""%PROTOC%" --version"`) do set "PROTOC_VERSION=%%v"

if not "%PROTOC_VERSION%"=="%EXPECTED_PROTOC%" (
  echo [build][ERROR] protoc 版本不符
  echo [build][ERROR]   expected: %EXPECTED_PROTOC%
  echo [build][ERROR]   actual  : %PROTOC_VERSION%
  echo [build][ERROR] 换用其它版本会改变生成产物，请使用本目录下的 protoc.exe。
  exit /b 1
)
echo [build] protoc 版本校验通过: %PROTOC_VERSION%

REM ---------------- 3) PMNetGen 预编译 ----------------
REM 行尾归一与 PMNet 生成都依赖它，两种模式下都先确保可用。

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [build][ERROR] 未找到 dotnet，无法执行 PMNet 代码生成与行尾归一。
  echo [build][ERROR] PMNetGen 为 .NET 控制台程序，见 Tools\PMNetGen。
  exit /b 1
)

if "%CHECK_ONLY%"=="1" goto :after_build_pmnetgen

echo [build] 编译 PMNetGen...
dotnet build "%PMNETGEN_PROJ%" -c Release -v q --nologo
if errorlevel 1 (
  echo [build][ERROR] PMNetGen 编译失败
  exit /b 1
)

:after_build_pmnetgen
if not exist "%PMNETGEN_DLL%" (
  echo [build][ERROR] 未找到 PMNetGen 产物: %PMNETGEN_DLL%
  echo [build][ERROR] 请先执行不带 --check-only 的 build.bat。
  exit /b 1
)

REM ---------------- 4) protoc 生成 ----------------
REM 必须在脚本目录下用裸文件名调用，理由见文件头注释 A。

if "%CHECK_ONLY%"=="1" goto :protoc_determinism

if not exist "%OUT_TOOL%"   mkdir "%OUT_TOOL%"   >nul 2>nul
if not exist "%OUT_CLIENT%" mkdir "%OUT_CLIENT%" >nul 2>nul
if not exist "%OUT_SERVER%" mkdir "%OUT_SERVER%" >nul 2>nul

echo [build] protoc 生成中...
pushd "%SCRIPT_DIR%"

"%PROTOC%" --csharp_out="%OUT_TOOL%"   "%PROTO_NAME%"
if errorlevel 1 (
  popd
  echo [build][ERROR] protoc 生成工具目录产物失败: %OUT_TOOL%
  exit /b 1
)

"%PROTOC%" --csharp_out="%OUT_CLIENT%" "%PROTO_NAME%"
if errorlevel 1 (
  popd
  echo [build][ERROR] protoc 生成客户端产物失败: %OUT_CLIENT%
  exit /b 1
)

"%PROTOC%" --csharp_out="%OUT_SERVER%" "%PROTO_NAME%"
if errorlevel 1 (
  popd
  echo [build][ERROR] protoc 生成服务端产物失败: %OUT_SERVER%
  exit /b 1
)

popd

REM ---------------- 5) protoc 产物行尾归一 ----------------

if not exist "%OUT_TOOL%\SocketProto.cs" (
  echo [build][ERROR] protoc 产物缺失: %OUT_TOOL%\SocketProto.cs
  exit /b 1
)
dotnet "%PMNETGEN_DLL%" --normalize-eol "%OUT_TOOL%\SocketProto.cs"
if errorlevel 1 (
  echo [build][ERROR] 工具目录产物行尾归一失败
  exit /b 1
)
dotnet "%PMNETGEN_DLL%" --normalize-eol "%OUT_CLIENT%\SocketProto.cs"
if errorlevel 1 (
  echo [build][ERROR] 客户端产物行尾归一失败
  exit /b 1
)
dotnet "%PMNETGEN_DLL%" --normalize-eol "%OUT_SERVER%\SocketProto.cs"
if errorlevel 1 (
  echo [build][ERROR] 服务端产物行尾归一失败
  exit /b 1
)

REM ---------------- 6) protoc 生成确定性校验 ----------------
REM 重新生成到临时目录后与已提交产物比对（文本模式 fc，不区分 CRLF/LF）：
REM 不一致说明产物并非由当前 proto + 当前 protoc 产出，例如忘了重新生成。

:protoc_determinism
if exist "%TMP_DETERMINISM%" rd /s /q "%TMP_DETERMINISM%"
mkdir "%TMP_DETERMINISM%" >nul 2>nul

pushd "%SCRIPT_DIR%"
"%PROTOC%" --csharp_out="%TMP_DETERMINISM%" "%PROTO_NAME%" >nul
if errorlevel 1 (
  popd
  echo [build][ERROR] protoc 临时生成失败
  exit /b 1
)
popd

REM 临时产物同样归一到 CRLF，使下面的比对成为精确的二进制比对。
REM 不要改用文本模式的 fc 来「容忍」行尾差异：实测其行为不可靠。
dotnet "%PMNETGEN_DLL%" --normalize-eol "%TMP_DETERMINISM%\SocketProto.cs" >nul
if errorlevel 1 (
  echo [build][ERROR] 临时产物行尾归一失败
  exit /b 1
)

if not exist "%OUT_CLIENT%\SocketProto.cs" (
  echo [build][ERROR] 客户端 protoc 产物缺失: %OUT_CLIENT%\SocketProto.cs
  exit /b 1
)

fc /b "%TMP_DETERMINISM%\SocketProto.cs" "%OUT_CLIENT%\SocketProto.cs" >nul
if errorlevel 1 (
  echo [build][ERROR] 客户端 SocketProto.cs 与权威 proto 不同步
  echo [build][ERROR] 请执行不带 --check-only 的 build.bat 重新生成。
  exit /b 1
)

if not exist "%OUT_SERVER%\SocketProto.cs" (
  echo [build][ERROR] 服务端 protoc 产物缺失: %OUT_SERVER%\SocketProto.cs
  exit /b 1
)

fc /b "%TMP_DETERMINISM%\SocketProto.cs" "%OUT_SERVER%\SocketProto.cs" >nul
if errorlevel 1 (
  echo [build][ERROR] 服务端 SocketProto.cs 与权威 proto 不同步
  echo [build][ERROR] 请执行不带 --check-only 的 build.bat 重新生成。
  exit /b 1
)

rd /s /q "%TMP_DETERMINISM%" >nul 2>nul
echo [build] protoc 产物同步校验通过

REM ---------------- 7) PMNet 生成 ----------------

if "%CHECK_ONLY%"=="1" goto :pmnet_check

if not exist "%OUT_PMNET_DIR%" mkdir "%OUT_PMNET_DIR%" >nul 2>nul

echo [build] PMNet 生成中...
dotnet "%PMNETGEN_DLL%" --proto "%PROTO%" --source-path "%PMNET_PROTO_REL%" --out "%OUT_PMNET%"
if errorlevel 1 (
  echo [build][ERROR] PMNet 代码生成失败
  exit /b 1
)

REM ---------------- 8) PMNet 产物同步校验 ----------------
REM 复用生成器的 --check：重新生成到内存并与磁盘产物比对（比对时行尾无关），
REM 既能发现「proto 改了没重新生成」，也能发现生成不确定。

:pmnet_check
dotnet "%PMNETGEN_DLL%" --proto "%PROTO%" --source-path "%PMNET_PROTO_REL%" --check "%OUT_PMNET%"
if errorlevel 1 (
  echo [build][ERROR] PMNet 产物与权威 proto 不同步
  exit /b 1
)

echo.
echo [build] OK
exit /b 0
