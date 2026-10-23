#!/usr/bin/env bash
# 构建并测试全部项目；CI 与本地验证使用。
set -euo pipefail

cd "$(dirname "$0")/.."

echo "==> 还原"
dotnet restore AiTools.slnx

echo "==> 构建（Release）"
dotnet build AiTools.slnx -c Release --no-restore

echo "==> 测试"
dotnet test tests/AiTools.Tests/AiTools.Tests.csproj -c Release --no-build

echo "==> 示例程序编译"
dotnet build samples/AiTools.Samples.Console/AiTools.Samples.Console.csproj -c Release --no-restore

echo "全部通过。"
