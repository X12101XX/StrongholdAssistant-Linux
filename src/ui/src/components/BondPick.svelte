<script lang="ts">
  // 盟约选择（v1.2.7 统一）：图鉴筛选、编辑器的目标盟约和筛选、禁用挑选、广场和森空岛筛选都用这一个。
  // 分「核心盟约」「附加盟约」两组，每格是图标 + 名字，每格最少 84px，放不下就换行，侧栏里一行约 4 个。
  // 各处的选法（多选、单选、点一下换档位）由调用方的 state / onpick 决定，样子都一样
  import type { Snippet } from 'svelte'
  import { app } from '../lib/state.svelte'
  import BondIcon from './BondIcon.svelte'

  type PickState = {
    on?: boolean
    badge?: string // 角标，如档位 I / II
    inc?: boolean // 本局阵容不完整：红色细框
    dim?: boolean // 不在可选范围：变淡
    disabled?: boolean
  }
  type Props = {
    state: (id: string) => PickState
    onpick: (id: string) => void
    /** 只列一组（森空岛核心、附加分开选时用），不画组标题 */
    kind?: 'core' | 'extra'
    /** 组标题右边的附加内容，如「用目标阵容的」 */
    coreAside?: Snippet
    extraAside?: Snippet
  }
  let { state, onpick, kind, coreAside, extraAside }: Props = $props()
  const groups = $derived(
    (kind ? [kind] : (['core', 'extra'] as const)).map((k) => ({
      k,
      label: k === 'core' ? '核心盟约' : '附加盟约',
      list: app.catalog.covenants.filter((c) => c.kind === k),
      aside: k === 'core' ? coreAside : extraAside,
    })),
  )
</script>

<div class="bondpick">
  {#each groups as g (g.k)}
    {#if !kind}
      <div class="gh"><span>{g.label}</span>{@render g.aside?.()}</div>
    {/if}
    <div class="grid">
      {#each g.list as c (c.id)}
        {@const s = state(c.id)}
        <button class="bp" class:on={s.on} class:inc={s.inc} class:dim={s.dim} disabled={s.disabled} title={c.name} onclick={() => onpick(c.id)}>
          <BondIcon id={c.id} size={22} on={s.on} />
          <span class="nm">{c.name}</span>
          {#if s.badge}<span class="lv term">{s.badge}</span>{/if}
        </button>
      {/each}
    </div>
  {/each}
</div>

<style>
  .bondpick {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .gh {
    display: flex;
    align-items: center;
    gap: 8px;
    justify-content: space-between;
    margin-top: 2px;
    font-size: var(--fs-xs);
    color: var(--fg-4);
  }
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(84px, 1fr));
    gap: 2px;
  }
  .bp {
    position: relative;
    display: flex;
    align-items: center;
    gap: 4px;
    min-width: 0;
    height: 28px;
    padding: 0 4px;
    font-size: var(--fs-sm);
    color: var(--fg-3);
  }
  .nm {
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .bp:hover:not(:disabled) {
    color: var(--fg-1);
    background: var(--bg-2);
  }
  .bp.on {
    color: var(--fg-0);
    background: var(--mint-tint);
    box-shadow: inset 0 0 0 1px var(--mint-lo);
  }
  .bp.inc {
    box-shadow: inset 0 0 0 1px rgb(var(--red-rgb) / 0.6);
  }
  .bp.dim {
    opacity: 0.35;
  }
  .bp:disabled {
    cursor: default;
  }
  .lv {
    margin-left: auto;
    flex: none;
    font-size: var(--fs-3xs);
    line-height: 1;
    padding: 2px 3px 1px;
    color: var(--gold-ink);
    background: var(--gold);
  }
</style>
