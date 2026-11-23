<script lang="ts">
  import { flip } from 'svelte/animate'
  import { scale } from 'svelte/transition'
  import { cubicOut } from 'svelte/easing'
  import { app } from '../lib/state.svelte'
  import { asset } from '../lib/assets'
  import { openMenu, operatorMenu, equipmentMenu, targetMenu } from '../lib/menu.svelte'
  import Icon from './Icon.svelte'
  import TierBadge from './TierBadge.svelte'
  import { allStages, stageLabel } from '../lib/stages'
  import { field } from '../lib/field.svelte'

  // 常驻顶栏：追踪列表 = 商店里会标记的干员和装备，任何页面都可见。
  // 不做「待购」和盟约进度：干员会反复买卖，盟约有没有生效游戏里也看得到。点头像取消追踪
  const t = $derived(app.target)
  // 有阶段时标题后面写当前阶段
  const stageName = $derived.by(() => {
    if (!t?.stages?.length) return ''
    const s = allStages(t)[app.stageIx]
    return s.final ? '成型' : stageLabel(s)
  })
  const keys = $derived(new Set(app.active?.members.filter((m) => m.key).map((m) => m.name) ?? []))
  // 「一组」干员（v1.0.1）：拿到的人数（本局有的人里没卖的）没够 need 时，组里没拿到、没被禁的都在商店标记（宿主算）；够了就不标
  const owned = $derived(new Set(field.state?.roster?.filter((r) => !r.sold).map((r) => r.operator) ?? []))
  const groups = $derived(
    (app.active?.groups ?? []).map((g) => {
      const got = g.members.filter((m) => owned.has(m.name)).length
      return { ...g, got, done: got >= g.need }
    }),
  )
  const groupTracked = $derived(groups.filter((g) => !g.done).flatMap((g) => g.members.filter((m) => !owned.has(m.name) && !app.banned.has(m.name))).length)
  const total = $derived(app.trackedOps.length + app.session.trackedEquipment.length + groupTracked)
  // 向上收起时点目标名也先展开
  const goTarget = () => (app.collapsed && app.setCollapsed(false), app.setTab('lineup'), (app.lineupView = 'target'))
  const fx = { duration: 160, easing: cubicOut, start: 0.6 }
  // rail：工作台左栏里竖着放。目标名在左栏上面单独显示，这里只写「追踪」；头像一行排满就换行，多了在栏里滚
  let { rail = false }: { rail?: boolean } = $props()
</script>

{#if t || total || !app.folded['hint.noTarget']}
<section class="tracker" class:rail>
  {#if t || total}
    <!-- 右键标题行：换目标、切阶段、全部追踪、清空追踪 -->
    <div class="head" role="presentation" oncontextmenu={(e) => openMenu(e, targetMenu())}>
      <span class="lbl term">Track</span>
      {#if rail}
        <span class="name muted">追踪</span>
      {:else if t}
        <button class="name" onclick={goTarget} title="目标阵容：{t.name}">{t.name}{#if stageName}<em class="stg">{stageName}</em>{/if}</button>
      {:else}
        <span class="name muted">单独追踪</span>
      {/if}
      <span class="spacer"></span>
      {#if app.untrackedInTarget}
        <button class="re" onclick={() => app.trackTarget()} title="把目标名单里取消过的重新全部追踪">
          <Icon name="refresh" size={12} />全部追踪<em>{app.untrackedInTarget}</em>
        </button>
      {/if}
      <button class="count" title="在图鉴里看追踪中的干员和装备" onclick={() => (app.setTab('codex'), (app.codexTracked = true))}><b>{total}</b> 项</button>
    </div>
    <div class="row">
      {#each app.trackedOps as n (n)}
        <button
          class="av"
          class:banned={app.banned.has(n)}
          class:key={keys.has(n)}
          title="{n}{keys.has(n) ? '（关键）' : ''}：点一下取消追踪"
          onclick={() => app.toggleTracked(n)}
          oncontextmenu={(e) => openMenu(e, operatorMenu(n))}
          animate:flip={{ duration: 200 }}
          transition:scale={fx}
        >
          <img src={asset.operator(n)} alt={n} />
          <span class="tr"><TierBadge tier={app.op(n).tier} size={12} /></span>
          {#if app.banned.has(n)}<img class="ban" src={asset.stage('img_chess_banned')} alt="禁用" />{/if}
          <span class="x"><Icon name="close" size={14} /></span>
        </button>
      {/each}
      {#each groups as g, gi (gi)}
        <span class="grp" class:done={g.done} title={g.done ? `组里任选 ${g.need} 个，已经拿到 ${g.got} 个，剩下的不再标记` : `组里任选 ${g.need} 个，拿到 ${g.got} 个；没拿够时组里的人都在商店标记`}>
          <em>{g.done ? '够了' : `任${g.need}`}</em>
          {#each g.members as m (m.name)}
            <span class="av gm" class:got={owned.has(m.name)} class:banned={app.banned.has(m.name)} class:key={m.key}>
              <img src={asset.operator(m.name)} alt={m.name} />
              <span class="tr"><TierBadge tier={app.op(m.name).tier} size={12} /></span>
              {#if app.banned.has(m.name)}<img class="ban" src={asset.stage('img_chess_banned')} alt="禁用" />{/if}
              {#if owned.has(m.name)}<b class="ok"><Icon name="check" size={11} /></b>{/if}
            </span>
          {/each}
        </span>
      {/each}
      {#if (app.trackedOps.length || groups.length) && app.session.trackedEquipment.length}<i class="div"></i>{/if}
      {#each app.session.trackedEquipment as n (n)}
        <button
          class="av eq"
          title="{n}：点一下取消追踪"
          onclick={() => app.toggleTrackedEq(n)}
          oncontextmenu={(e) => openMenu(e, equipmentMenu(n))}
          animate:flip={{ duration: 200 }}
          transition:scale={fx}
        >
          <img src={asset.equipment(n)} alt={n} />
          <span class="x"><Icon name="close" size={14} /></span>
        </button>
      {/each}
      {#if !total}
        <span class="none">没有追踪的了，商店里不会标记。</span>
      {/if}
    </div>
  {:else}
    <div class="empty">
      还没有追踪。去
      <button onclick={goTarget}>阵容</button>
      设个目标，或在详情、右键菜单里「追踪」，商店里会标记这些。
      <button class="close" title="不再提示" aria-label="不再提示" onclick={() => app.toggleFold('hint.noTarget')}><Icon name="close" size={13} /></button>
    </div>
  {/if}
</section>
{/if}

<style>
  .tracker {
    flex: none;
    position: relative;
    padding: 7px 10px 8px;
    background:
      var(--noise),
      linear-gradient(180deg, var(--bg-1a), var(--bg-1));
    border-bottom: 1px solid var(--line);
  }
  .tracker::before {
    /* 左侧薄荷绿竖条 */
    content: '';
    position: absolute;
    left: 0;
    top: 8px;
    bottom: 8px;
    width: 2px;
    background: var(--mint);
    box-shadow: var(--mint-glow);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-bottom: 6px;
    min-height: 20px;
  }
  .lbl {
    font-size: var(--fs-2xs);
    color: var(--mint);
  }
  .name {
    font-size: var(--fs-md);
    font-weight: var(--fw-bold);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    min-width: 0;
  }
  button.name:hover {
    color: var(--mint-hi);
  }
  .name.muted {
    color: var(--fg-3);
    font-weight: var(--fw-base);
  }
  .spacer {
    flex: 1;
  }
  .re {
    flex: none;
    display: flex;
    align-items: center;
    gap: 3px;
    font-size: var(--fs-sm);
    padding: 2px 6px;
    color: var(--fg-3);
    box-shadow: inset 0 0 0 1px var(--line);
  }
  .re em {
    font-style: normal;
    font-family: var(--font-term);
    color: var(--gold);
    margin-left: 1px;
  }
  .re:hover {
    color: var(--mint);
    box-shadow: inset 0 0 0 1px var(--mint-lo);
  }
  .count {
    flex: none;
    font-size: var(--fs-sm);
    color: var(--fg-3);
    white-space: nowrap;
  }
  .count:hover {
    color: var(--fg-1);
    text-decoration: underline;
  }
  .count b {
    color: var(--mint);
    font-family: var(--font-term);
    font-size: var(--fs-md);
  }
  /* 最多两行，再多就在顶栏里滚动，不把页面往下挤 */
  .row {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px;
    max-height: 68px;
    overflow-y: auto;
    scrollbar-width: none;
  }
  .av {
    position: relative;
    flex: none;
    width: 32px;
    height: 32px;
    background: var(--bg-3);
    box-shadow:
      inset 0 0 0 1px var(--mint-lo),
      0 0 5px rgb(var(--mint-rgb) / 0.18);
    overflow: hidden;
  }
  .av img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
  .av.eq {
    background: radial-gradient(circle at 50% 45%, var(--bg-4), var(--bg-2) 70%);
  }
  .av.eq img {
    object-fit: contain;
    padding: 3px;
  }
  /* 悬停：盖一层暗底和叉，表示点了就取消 */
  /* 本局禁用：头像压暗变灰、描红边 */
  .av.banned {
    box-shadow: inset 0 0 0 1.5px var(--red);
  }
  .av.banned img:not(.ban) {
    filter: grayscale(1) brightness(0.42);
  }
  /* 禁用角标：素材四周有留白，放大并往右上移，让红三角卡在角上 */
  .av .ban {
    position: absolute;
    right: -4px;
    top: -4px;
    z-index: 1;
    width: 19px;
    height: 19px;
  }
  .x {
    position: absolute;
    inset: 0;
    z-index: 2;
    display: grid;
    place-items: center;
    color: var(--fg-0);
    background: rgb(var(--bg-0-rgb) / 0.62);
    box-shadow: inset 0 0 0 1.5px var(--red);
    opacity: 0;
    transition: opacity var(--t-fast);
  }
  .av:hover .x {
    opacity: 1;
  }
  .av.key::after {
    /* 关键干员：右上角金色三角（左上角给等阶，v1.2.7） */
    content: '';
    position: absolute;
    top: 0;
    right: 0;
    width: 9px;
    height: 9px;
    background: var(--gold);
    clip-path: polygon(0 0, 100% 0, 100% 100%);
  }
  /* 等阶：左上角小角标，打商店时一眼看出几阶（v1.2.7） */
  .av .tr {
    position: absolute;
    left: 0;
    top: 0;
    z-index: 1;
    display: flex;
    filter: drop-shadow(0 0 1px rgb(var(--shade-rgb) / 0.8));
  }
  /* 「一组」干员：一组框在一起，左边写「任 N」；拿到的打勾，够了整组压暗 */
  .grp {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 2px 3px 2px 0;
    box-shadow: inset 0 0 0 1px rgb(var(--mint-rgb) / 0.35);
    transition: opacity var(--t-fast);
  }
  .grp em {
    padding: 0 4px;
    font-style: normal;
    font-size: var(--fs-xs);
    color: var(--mint);
    white-space: nowrap;
  }
  .grp.done {
    opacity: 0.45;
    box-shadow: inset 0 0 0 1px var(--line);
  }
  .grp.done em {
    color: var(--fg-3);
  }
  .av.gm.got img {
    filter: brightness(0.55);
  }
  .ok {
    position: absolute;
    right: 0;
    bottom: 0;
    display: grid;
    place-items: center;
    width: 14px;
    height: 14px;
    color: var(--mint-ink, #04110d);
    background: var(--mint);
  }
  .div {
    width: 1px;
    height: 22px;
    margin: 0 2px;
    background: var(--line);
  }
  .none {
    font-size: var(--fs-sm);
    color: var(--fg-4);
    line-height: 32px;
  }
  .empty {
    position: relative;
    font-size: var(--fs-base);
    color: var(--fg-3);
    padding: 4px 24px 2px 0;
  }
  .empty button:not(.close) {
    color: var(--mint);
    text-decoration: underline;
    text-underline-offset: 2px;
  }
  .close {
    position: absolute;
    top: 0;
    right: -4px;
    width: 22px;
    height: 22px;
    display: grid;
    place-items: center;
    color: var(--fg-4);
  }
  .close:hover {
    color: var(--fg-1);
    background: var(--bg-3);
  }
  /* 工作台左栏 */
  .rail {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
    padding: 10px 12px;
    background: none;
    border-bottom: none;
  }
  .rail::before {
    display: none;
  }
  .rail .row {
    flex: 1;
    min-height: 0;
    max-height: none;
    align-content: flex-start;
    scrollbar-width: thin;
  }
  .stg {
    margin-left: 6px;
    font-style: normal;
    font-size: var(--fs-sm);
    font-weight: var(--fw-base);
    color: var(--mint);
  }
</style>
