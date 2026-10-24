/**
 * 宿主环境的极简环境声明。
 *
 * 脚本扩展以 CommonJS 形式导出（`module.exports = ...`），而 TypeScript 只有引入
 * `@types/node` 才认识 `module`。示例包不想为一个全局变量加开发依赖，因此在这里
 * 声明本包用到的那一点点环境形状；仅供 `tsc --noEmit` 使用，不产生任何运行时代码。
 */

declare var module: { exports: unknown };
