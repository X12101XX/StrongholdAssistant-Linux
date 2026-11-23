<script lang="ts">
  import { roster } from '../lib/roster.svelte'
  import Icon from './Icon.svelte'
  import OpTile from './OpTile.svelte'
  import { app } from '../lib/state.svelte'

  // 图鉴干员页顶上的「我的干员」（任务 163）：没读过时说明怎么读；读取中显示进度和停止；读过显示结果
  interface Props {
    /** 「n 名没认全」：只看这些干员 */
    onlyIncomplete: boolean
    ontoggle: () => void
  }
  let { onlyIncomplete, ontoggle }: Props = $props()
  const r = $derived(roster.data)
  const s = $derived(roster.scan)
  const when = (iso: string) => {
    const d = new Date(iso)
    return `${d.getMonth() + 1}-${d.getDate()} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
  }
</script>

<section class="roster" class:busy={roster.running}>
  {#if roster.running}
    <div class="row">
      <span class="ic spin"><Icon name="scan" size={16} /></span>
      <b>正在读取我的干员</b>
      <span class="num">已读 <em>{s?.read ?? 0}</em> 名</span>
      <button class="act stop" onclick={() => roster.stop()}><Icon name="stop" size={13} />停止</button>
    </div>
    <div class="bar"><i></i></div>
    <p class="rtip">助手在游戏里左右滑动列表，这时别点鼠标、别按键、别大幅挪鼠标，不然会停下，读到的会留下。</p>
  {:else if !r.readAt}
    <div class="row">
      <span class="ic"><Icon name="scan" size={16} /></span>
      <b>读取我的干员</b>
      <button class="act go" disabled={roster.starting} onclick={() => roster.start()}>{roster.starting ? '认页面…' : '读取'}</button>
    </div>
    <p class="rtip">在游戏里打开卫戍协议的「物资调配」页，点读取。助手会自己把排序切到「按等阶」、滑动列表，读出每名干员的皮肤、技能、模组、潜能，技能和模组同时设为我的默认。读的时候别点鼠标、别按键。</p>
    {@render admin()}
  {:else}
    <div class="row">
      <span class="ic ok"><Icon name="check" size={16} /></span>
      <b>我的干员</b>
      <span class="num"><em>{r.operators.length}</em> 名 · {when(r.readAt)}</span>
      {#if roster.incomplete.length}
        <button class="miss" class:on={onlyIncomplete} onclick={ontoggle}>{roster.incomplete.length} 名没认全</button>
      {/if}
      <button class="act" disabled={roster.starting} onclick={() => roster.start()}><Icon name="refresh" size={13} />{roster.starting ? '认页面…' : '重新读取'}</button>
    </div>
    {#if r.customSquad.length}
      <div class="squad">
        <span class="lbl">自选编队</span>
        {#each r.customSquad as n (n)}<OpTile name={n} size={28} showName={false} onclick={() => (app.detail = { kind: 'operator', name: n })} />{/each}
      </div>
    {/if}
    {@render admin()}
  {/if}
</section>

<!-- 游戏是管理员身份、助手不是时，Windows 不让助手操作游戏，滑不动（162 实测） -->
{#snippet admin()}
  {#if app.keysBlocked}
    <p class="rtip warn">
      游戏是以管理员身份运行的，助手也要以管理员身份运行才能滑动列表。<button class="lnk" onclick={() => { app.settingsTab = 'keys'; app.settingsOpen = true }}>去设置里打开</button>
    </p>
  {/if}
{/snippet}

<style>
  .roster {
    margin: 2px 0 8px;
    padding: 8px 10px;
    background: var(--bg-1);
    box-shadow: inset 0 0 0 1px var(--line);
  }
  .roster.busy {
    box-shadow: inset 0 0 0 1px var(--mint);
  }
  .row {
    display: flex;
    align-items: center;
    gap: 6px;
    min-height: 26px;
  }
  .ic {
    display: grid;
    color: var(--fg-3);
  }
  .ic.ok,
  .busy .ic {
    color: var(--mint);
  }
  .spin {
    animation: pulse 1.2s ease-in-out infinite;
  }
  @keyframes pulse {
    50% {
      opacity: 0.35;
    }
  }
  b {
    font-size: var(--fs-md);
    color: var(--fg-1);
    white-space: nowrap;
  }
  .num {
    font-size: var(--fs-sm);
    color: var(--fg-3);
    white-space: nowrap;
    font-variant-numeric: tabular-nums;
  }
  .num em {
    font-style: normal;
    color: var(--fg-1);
  }
  .act {
    margin-left: auto;
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 3px 10px;
    font-size: var(--fs-sm);
    color: var(--fg-2);
    background: var(--bg-2);
    white-space: nowrap;
  }
  .act:disabled {
    opacity: 0.45;
    cursor: default;
  }
  .act:hover:not(:disabled) {
    color: var(--fg-1);
  }
  .act.go {
    color: var(--mint-ink);
    background: var(--mint);
  }
  .act.stop {
    color: var(--red);
  }
  .miss {
    padding: 0 6px;
    font-size: var(--fs-xs);
    color: var(--gold);
    box-shadow: inset 0 0 0 1px var(--gold);
    white-space: nowrap;
  }
  .miss.on {
    color: var(--gold-ink);
    background: var(--gold);
  }
  .rtip {
    margin: 6px 0 0;
    font-size: var(--fs-sm);
    line-height: 1.5;
    color: var(--fg-4);
  }
  .rtip.warn {
    color: var(--gold);
  }
  .lnk {
    margin-left: 4px;
    color: var(--mint);
    text-decoration: underline;
  }
  .bar {
    position: relative;
    height: 2px;
    margin-top: 7px;
    overflow: hidden;
    background: var(--bg-2);
  }
  .bar i {
    position: absolute;
    inset: 0 auto 0 0;
    width: 30%;
    background: var(--mint);
    animation: slide 1.1s ease-in-out infinite;
  }
  @keyframes slide {
    from {
      transform: translateX(-100%);
    }
    to {
      transform: translateX(340%);
    }
  }
  .squad {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 3px;
    margin-top: 6px;
  }
  .lbl {
    margin-right: 4px;
    font-size: var(--fs-sm);
    color: var(--fg-3);
  }
</style>
