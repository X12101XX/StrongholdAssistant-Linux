<script lang="ts">
  import { slide } from 'svelte/transition'
  import { cubicOut } from 'svelte/easing'
  import { app } from '../lib/state.svelte'
  import { asset } from '../lib/assets'
  import { api } from '../lib/api'
  import Icon from './Icon.svelte'
  import type { RoundEnemyOptions } from '../lib/api/types'

  // 本回合敌人（149 读到的；200 起可以手动补上 / 去掉）。
  // 游戏敌人列表常常一次读不全（敌人多要往下滚；选了悬赏后悬赏怪排第一，把别的挤下去），
  // 站位推荐靠这份名单筛怪组，所以让玩家补：候选先给本回合怪组表里的敌人（按还对得上的怪组排），再能搜全部敌人
  const sl = { duration: 200, easing: cubicOut }
  const m = $derived(app.match)
  const round = $derived(m.economy?.round ?? null)
  // 只认和当前回合对得上的那份；读到的是上一回合的就当这回合还没看
  const cur = $derived(m.roundEnemies && m.roundEnemies.round === round ? m.roundEnemies : null)
  const list = $derived(cur?.enemies ?? [])
  const removed = $derived(cur?.removed ?? [])
  let editing = $state(false)
  let q = $state('')
  // 只在对不上的怪组里出现的候选先收起来
  let showOther = $state(false)

  // 怪组表：回合变了重新取（站位推荐筛怪组用的同一份）
  let options = $state<RoundEnemyOptions | null>(null)
  $effect(() => {
    const r = round
    options = null
    if (r !== null) api.roundEnemyOptions().then((o) => r === round && (options = o))
  })
  const groups = $derived(options?.groups ?? [])
  const inGroups = $derived(new Set(groups.flatMap((g) => g.enemies.map((e) => e.name))))
  // 名单里出现在怪组表里的才拿来筛（悬赏怪、读错的不在表里，推荐时不算）
  const known = $derived(list.filter((e) => inGroups.has(e.name)).map((e) => e.name))
  const outside = $derived(options ? list.filter((e) => !inGroups.has(e.name)) : [])
  const outBounty = $derived(outside.filter((e) => bountyCoin.has(e.name)))
  const outOther = $derived(outside.filter((e) => !bountyCoin.has(e.name)))
  const matching = $derived(groups.filter((g) => known.every((n) => g.enemies.some((e) => e.name === n))))
  const matchW = $derived(matching.reduce((s, g) => s + g.weight, 0))
  // 候选：还对得上的怪组里出现的按出现机会排在前；别的怪组里的排后面、压暗
  const candidates = $derived.by(() => {
    const have = new Set(list.map((e) => e.name))
    const map = new Map<string, { id: string | null; name: string; w: number; all: number }>()
    for (const g of groups)
      for (const e of g.enemies) {
        if (have.has(e.name)) continue
        const c = map.get(e.name) ?? { id: e.id, name: e.name, w: 0, all: 0 }
        c.all += g.weight
        if (matching.includes(g)) c.w += g.weight
        map.set(e.name, c)
      }
    return [...map.values()].sort((a, b) => b.w - a.w || b.all - a.all)
  })
  const likely = $derived(known.length ? candidates.filter((c) => c.w > 0) : candidates)
  const other = $derived(known.length ? candidates.filter((c) => c.w === 0) : [])
  // 悬赏怪：名字对得上悬赏的敌人标出奖励（同一个敌人几档奖励不同时写范围）
  const bountyCoin = $derived.by(() => {
    const map = new Map<string, number[]>()
    for (const b of app.catalog.bounties ?? []) if (b.coin > 0) map.set(b.enemyName, [...(map.get(b.enemyName) ?? []), b.coin])
    return map
  })
  const coinText = (name: string) => {
    const c = bountyCoin.get(name)
    if (!c) return null
    const lo = Math.min(...c), hi = Math.max(...c)
    return lo === hi ? `+${lo}` : `+${lo}~${hi}`
  }
  // 搜全部敌人（含悬赏怪），同名只留一个
  const results = $derived.by(() => {
    const s = q.trim()
    if (!s) return []
    const have = new Set(list.map((e) => e.name))
    const seen = new Set<string>()
    return app.catalog.enemies.filter((e) => e.name.includes(s) && !have.has(e.name) && !seen.has(e.name) && seen.add(e.name)).slice(0, 12)
  })
  const nRead = $derived(list.filter((e) => e.source === 'read').length)
  const nManual = $derived(list.length - nRead)
  const pct = (w: number) => (matchW > 0 ? `${Math.max(1, Math.round((w / matchW) * 100))}%` : '')

  const add = (name: string) => app.setRoundEnemy(name, true)
  const remove = (name: string) => app.setRoundEnemy(name, false)
  const done = () => {
    editing = false
    q = ''
    showOther = false
  }
  // 换回合时收起补选
  $effect(() => {
    round
    done()
  })
</script>

{#snippet face(id: string | null, name: string)}
  <span class="vimg">{#if id}<img src={asset.enemy(id)} alt="" onerror={(ev) => ev.currentTarget.classList.add('miss')} />{/if}<b>{name.slice(0, 1)}</b></span>
{/snippet}
{#snippet coin(name: string)}
  {@const c = coinText(name)}
  {#if c}<span class="coin" title="悬赏怪：选了悬赏后打掉拿的资金" style:--coin="url({asset.hud('s_icon_gold')})"><i></i>{c}</span>{/if}
{/snippet}

{#if round !== null && (cur || editing)}
  <div class="rnd" class:editing>
    <div class="head">
      <span class="vlabel">第 {round} 回合敌人<small>{list.length} 种{#if nManual} · 读到 {nRead} · 补上 {nManual}{:else if list.length} · 游戏里敌人列表读到的{/if}</small></span>
      <button class="edit" class:on={editing} onclick={() => (editing ? done() : (editing = true))} title="游戏里列表没读全（要往下滚、悬赏怪把别的挤下去）时，在这里补上或去掉">
        <Icon name={editing ? 'check' : 'add'} size={13} />{editing ? '完成' : '补选'}
      </button>
    </div>
    {#if list.length}
      <div class="vgrid">
        {#each list as e (e.name)}
          {#if editing}
            <button class="vitem" class:manual={e.source === 'manual'} title="去掉{e.name}" onclick={() => remove(e.name)}>
              {@render face(e.id, e.name)}
              <span class="vname">{e.name}</span>
              {#if !inGroups.has(e.name)}{@render coin(e.name)}{/if}
              <span class="x"><Icon name="close" size={13} /></span>
            </button>
          {:else if e.id}
            <button class="vitem" class:manual={e.source === 'manual'} title="{e.name}：点开看资料" onclick={() => (app.detail = { kind: 'enemy', id: e.id! })}>
              {@render face(e.id, e.name)}
              <span class="vname">{e.name}</span>
              {#if !inGroups.has(e.name)}{@render coin(e.name)}{/if}
            </button>
          {:else}
            <span class="vitem" class:manual={e.source === 'manual'} title="资料里没有这个名字">
              {@render face(null, e.name)}
              <span class="vname">{e.name}</span>
            </span>
          {/if}
        {/each}
      </div>
    {:else}
      <p class="empty">还没有敌人，从下面点选这回合会出的</p>
    {/if}

    <!-- 名单和怪组表对一下：站位推荐按对得上的怪组算 -->
    {#if options && known.length}
      <p class="fit" class:bad={!matching.length} class:sure={matching.length === 1}>
        {#if !matching.length}
          名单和本回合怪组对不上，站位推荐按本局敌人类型算；看看有没有读错的，去掉
        {:else if matching.length === 1}
          能确定是「{matching[0].label}」，站位推荐按它算
        {:else}
          对得上 <b>{matching.length}</b> / {groups.length} 种怪组{#if !editing}，<button class="link" onclick={() => (editing = true)}>再补一两个</button>能缩小{/if}
        {/if}
      </p>
    {/if}
    {#if outBounty.length}
      <p class="fit note">{outBounty.map((e) => e.name).join('、')} 是悬赏怪，打掉拿资金；不在怪组表里，站位推荐暂不算它</p>
    {/if}
    {#if outOther.length}
      <p class="fit note">{outOther.map((e) => e.name).join('、')} 不在本回合怪组里，可能读错了，推荐时不算</p>
    {/if}

    {#if editing}
      <div class="picker" transition:slide={sl}>
        {#if removed.length}
          <div class="row">
            <span class="plabel">去掉的</span>
            {#each removed as e (e.name)}
              <button class="chip back" onclick={() => add(e.name)} title="放回名单">{e.name}<Icon name="refresh" size={12} /></button>
            {/each}
          </div>
        {/if}
        <span class="plabel">这回合可能出的<small>{options ? (known.length ? '还对得上的怪组里的排前面' : '按怪组表，出现机会大的在前') : '这回合没有怪组数据，用下面搜'}</small></span>
        {#snippet cands(cs: typeof candidates, dim: boolean)}
          <div class="cgrid">
            {#each cs as c (c.name)}
              <button class="cand" class:dim onclick={() => add(c.name)} title={dim ? `${c.name}：只在和现在名单对不上的怪组里` : `补上${c.name}`}>
                {@render face(c.id, c.name)}
                <span class="vname">{c.name}</span>
                {#if c.w > 0}<span class="vp">{pct(c.w)}</span>{/if}
              </button>
            {/each}
          </div>
        {/snippet}
        {#if likely.length}{@render cands(likely, false)}{:else if options && known.length}<p class="empty">还对得上的怪组里的敌人都在名单上了</p>{/if}
        {#if other.length}
          <button class="more" onclick={() => (showOther = !showOther)}>
            {showOther ? '收起' : `还有 ${other.length} 个只在对不上的怪组里`}<Icon name={showOther ? 'arrowUp' : 'arrowDown'} size={12} />
          </button>
          {#if showOther}<div transition:slide={sl}>{@render cands(other, true)}</div>{/if}
        {/if}
        <div class="search">
          <Icon name="search" size={14} />
          <input bind:value={q} placeholder="搜全部敌人（含悬赏怪）" />
        </div>
        {#if q.trim()}
          <div class="cgrid">
            {#each results as e (e.id)}
              <button class="cand" onclick={() => { add(e.name); q = '' }}>
                {@render face(e.id, e.name)}
                <span class="vname">{e.name}</span>
                {@render coin(e.name)}
              </button>
            {:else}
              <p class="empty">没有叫这个名字的敌人</p>
            {/each}
          </div>
        {/if}
      </div>
    {/if}
  </div>
{:else if round !== null}
  <!-- 这回合还没读到：提醒玩家在游戏里点一下（用户 2026-11-15：每回合提醒点「看怪物」） -->
  <div class="rhint">
    <Icon name="search" size={14} />
    <span>第 {round} 回合：把鼠标停在<b>回合数右边的放大镜</b>上一两秒，敌人名单就会读进来；也可以<button class="link" onclick={() => (editing = true)}>手动补选</button></span>
  </div>
{/if}

<style>
  .rnd {
    padding: 6px 8px 8px;
    background: var(--bg-1);
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .head {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .vlabel {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: 0 6px;
    font-size: var(--fs-sm);
    font-weight: var(--fw-bold);
    color: var(--fg-2);
  }
  .vlabel small,
  .plabel small {
    font-weight: var(--fw-base);
    color: var(--fg-4);
  }
  .edit {
    flex: none;
    display: flex;
    align-items: center;
    gap: 3px;
    padding: 2px 7px 2px 5px;
    font-size: var(--fs-sm);
    color: var(--fg-2);
    border: 1px solid var(--line);
  }
  .edit:hover {
    color: var(--fg-1);
    background: var(--bg-3);
  }
  .edit.on {
    color: var(--mint);
    border-color: var(--mint);
  }
  .vgrid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
    gap: 4px;
  }
  .vitem,
  .cand {
    position: relative;
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 3px 6px 3px 3px;
    background: var(--bg-2);
    min-width: 0;
    text-align: left;
  }
  .vitem:hover,
  .cand:hover {
    background: var(--bg-3);
  }
  /* 手动补上的：左边一道虚线，和读到的分开 */
  .vitem.manual {
    box-shadow: inset 2px 0 0 var(--mint);
  }
  .editing .vitem:hover {
    background: var(--red-tint);
  }
  .x {
    flex: none;
    color: var(--fg-4);
    display: grid;
  }
  .editing .vitem:hover .x {
    color: var(--red);
  }
  .vimg {
    position: relative;
    width: 30px;
    height: 30px;
    flex: none;
    display: grid;
    place-items: center;
    background: rgb(var(--hl-rgb) / 0.05);
  }
  .cand .vimg {
    width: 24px;
    height: 24px;
  }
  .vimg img {
    width: 100%;
    height: 100%;
    object-fit: contain;
    position: relative;
    z-index: 1;
  }
  .vimg img:global(.miss) {
    display: none;
  }
  .vimg b {
    position: absolute;
    font-size: var(--fs-sm);
    color: var(--fg-4);
  }
  .vname {
    flex: 1;
    min-width: 0;
    font-size: var(--fs-sm);
    line-height: 1.25;
    color: var(--fg-1);
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .vp {
    flex: none;
    font-size: var(--fs-2xs);
    color: var(--fg-4);
    font-variant-numeric: tabular-nums;
  }
  .coin {
    flex: none;
    display: flex;
    align-items: center;
    gap: 2px;
    font-size: var(--fs-2xs);
    font-weight: var(--fw-bold);
    color: var(--gold);
    font-variant-numeric: tabular-nums;
  }
  .coin i {
    width: 12px;
    height: 12px;
    background: var(--coin) center / contain no-repeat;
  }
  .empty {
    margin: 0;
    padding: 6px 2px;
    font-size: var(--fs-sm);
    color: var(--fg-4);
  }
  .fit {
    margin: 0;
    font-size: var(--fs-xs);
    color: var(--fg-3);
    line-height: 1.45;
  }
  .fit b {
    color: var(--fg-1);
  }
  .fit.sure {
    color: var(--mint);
  }
  .fit.bad {
    color: var(--red);
  }
  .fit.note {
    color: var(--fg-4);
  }
  .link {
    color: var(--mint);
    text-decoration: underline;
    text-underline-offset: 2px;
  }
  .picker {
    display: flex;
    flex-direction: column;
    gap: 5px;
    padding-top: 6px;
    border-top: 1px solid var(--line);
  }
  .plabel {
    display: flex;
    align-items: baseline;
    gap: 6px;
    font-size: var(--fs-xs);
    font-weight: var(--fw-bold);
    color: var(--fg-3);
  }
  .row {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px;
  }
  .chip {
    display: flex;
    align-items: center;
    gap: 3px;
    padding: 2px 6px;
    font-size: var(--fs-xs);
    color: var(--fg-3);
    background: var(--bg-2);
    text-decoration: line-through;
  }
  .chip:hover {
    color: var(--fg-1);
    text-decoration: none;
  }
  .cgrid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(110px, 1fr));
    gap: 3px;
  }
  .cand {
    gap: 5px;
    padding: 2px 5px 2px 2px;
  }
  .cand .vname {
    font-size: var(--fs-xs);
  }
  .more {
    align-self: flex-start;
    display: flex;
    align-items: center;
    gap: 3px;
    font-size: var(--fs-xs);
    color: var(--fg-3);
  }
  .more:hover {
    color: var(--fg-1);
  }
  .cand.dim {
    opacity: 0.45;
  }
  .search {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 0 8px;
    height: 28px;
    background: var(--bg-2);
    color: var(--fg-4);
    margin-top: 2px;
  }
  .search input {
    flex: 1;
    min-width: 0;
    background: none;
    border: 0;
    outline: 0;
    color: var(--fg-1);
    font: inherit;
    font-size: var(--fs-sm);
  }
  .rhint {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 7px 9px;
    font-size: var(--fs-sm);
    color: var(--fg-3);
    border: 1px dashed var(--line);
  }
  .rhint b {
    color: var(--fg-1);
    font-weight: var(--fw-semi);
  }
</style>
