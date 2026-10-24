import { suffix as addSuffix } from './lib/suffix';

interface Arguments {
  raw: string;
}

interface StateStore {
  get<T>(key: string, defaultValue: T): T;
  set<T>(key: string, value: T): void;
}

interface Context {
  state: StateStore;
}

interface PromptEvent {
  promptSections: string[];
}

interface ToolArguments {
  value: string;
}

interface ToolDefinition {
  name: string;
  description: string;
  parameters: Record<string, unknown>;
}

interface ExtensionApi {
  registerCommand(name: string, description: string, handler: (ctx: Context, args: Arguments) => Promise<unknown>): void;
  registerTool(definition: ToolDefinition, execute: (args: ToolArguments, ctx: Context) => Promise<unknown>, options?: Record<string, unknown>): void;
  on(name: string, handler: (event: PromptEvent, ctx: Context) => Promise<void>): void;
}

type Counter = number;

enum Stage {
  Start,
  Done = 4
}

declare const hostProvidedValue: string;

function copyItems<T>(items: T[]): T[] {
  const result: T[] = [];
  for (const item of items) {
    result.push(item);
  }
  return result;
}

function optionalSuffix(value?: string): string {
  return addSuffix(value ?? 'default');
}

const asserted: string | null = copyItems(['ts' as string])[0];
const nonNull = asserted!;
const result: Result = { text: 'result' };
const counter: Counter = 1;
const stage: Stage = Stage.Start;

module.exports = {
  name: 'script-ts',
  description: 'TypeScript fixture extension',
  activate(api: ExtensionApi) {
    api.registerCommand('script-ts-hello', 'Greet from TypeScript', async (ctx: Context, args: Arguments): Promise<unknown> => {
      const values = copyItems([args.raw]);
      ctx.state.set('tsName', values[0] + optionalSuffix('ok'));
      const branch = counter ? 'yes' : 'no';
      return { display: `ts ${args.raw} ${nonNull} ${Stage.Done} ${stage} ${result.text} ${branch}` };
    });
    api.registerTool({
      name: 'script_ts_echo',
      description: 'Echo a TypeScript value',
      parameters: { type: 'object', properties: { value: { type: 'string' } } }
    }, async (args: ToolArguments, ctx: Context) => ({ value: args.value, suffix: addSuffix('tool') }));
    api.on('before_agent_start', async (event: PromptEvent, ctx: Context): Promise<void> => {
      event.promptSections.push(`TS state=${ctx.state.get('tsName', 'none')}`);
    });
  }
};
