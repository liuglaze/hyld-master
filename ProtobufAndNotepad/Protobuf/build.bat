REM 权威协议源：本目录下的 SocketProto.proto
REM 运行时生成产物：../../Client/Assets/Scripts/Server/SocketProto.cs 与 ../../Server/Server/SocketProto.cs
REM 工具目录产物：./CSharp/** 仅供工具/中间产物使用，不视为运行时权威文件
protoc --csharp_out=./CSharp SocketProto.proto
protoc --csharp_out=../../Client/Assets/Scripts/Server SocketProto.proto
protoc --csharp_out=../../Server/Server SocketProto.proto