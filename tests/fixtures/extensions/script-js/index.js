module.exports = {
  name: 'script-js',
  description: 'JavaScript fixture extension',
  activate(api) {
    api.registerCommand('script-hello', 'Greet from JavaScript', async (ctx, args) => {
      const count = (ctx.state.get('count', 0) || 0) + 1;
      ctx.state.set('count', count);
      return { display: `hello ${args.raw} (${count})` };
    });
    api.registerCommand('script-spin', 'Loop forever for timeout tests', async () => {
      while (true) { }
    });
    api.registerCommand('script-fs', 'Check forbidden filesystem module', async () => {
      require('fs');
      return 'unexpected';
    });
    api.registerCommand('script-ok', 'Remain usable after timeout', async () => ({ display: 'usable' }));
    api.registerTool({
      name: 'script_echo',
      description: 'Echo a JSON value',
      parameters: { type: 'object', properties: { value: { type: 'string' } } }
    }, async (args, ctx) => {
      ctx.state.set('toolValue', args.value);
      return { echoed: args.value };
    });
    api.on('before_agent_start', async (event, ctx) => {
      event.promptSections.push(`JS count=${ctx.state.get('count', 0)}`);
    });
  }
};
