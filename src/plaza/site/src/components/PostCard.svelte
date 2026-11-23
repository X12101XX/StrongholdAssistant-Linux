<script lang="ts">
  import type { Post } from '../api'
  import { link, router } from '../router.svelte'
  import { ago, asset, strategy } from '../data'
  import OpAvatar from './OpAvatar.svelte'
  import BondChip from './BondChip.svelte'
  import Icon from './Icon.svelte'

  // 列表里的一张阵容卡，整张可点进详情
  let { post }: { post: Post } = $props()
  const l = $derived(post.lineup)
  const st = $derived(strategy(l.strategyId))
  // 作者名（v1.0.1）：卡片本身是链接，作者名不能再套 <a>，点了拦下来改去「只看这个作者」
  const byAuthor = (e: Event) => {
    e.preventDefault()
    e.stopPropagation()
    router.go(`/plaza?author=${encodeURIComponent(post.author.id)}&by=${encodeURIComponent(post.author.nickname)}`)
  }
  const pri = (m: (typeof l.members)[number]) => m.equipment?.filter((x) => x.priority).map((x) => x.name) ?? []
</script>

<a class="pc" class:featured={post.featured} class:old={!post.current} href="/p/{post.id}" use:link>
  <div class="hd">
    <b class="nm">{l.name}</b>
    {#if post.featured}<span class="feat"><Icon name="star" size={11} />精选</span>{/if}
    {#if !post.current}<span class="oldtag">旧赛季</span>{/if}
    {#if l.stages?.length}<span class="stg" title="卡片上是成型阵容；前面还有 {l.stages.length} 个回合阶段">{l.stages.length + 1} 阶段</span>{/if}
  </div>
  <div class="by"><span class="who" role="link" tabindex="0" title="只看 {post.author.nickname} 发的阵容" onclick={byAuthor} onkeydown={(e) => e.key === 'Enter' && byAuthor(e)}>{post.author.nickname}</span> · {ago(post.createdAt)}</div>
  {#if l.goals.length}
    <div class="goals">
      {#each l.goals as g (g.id)}<BondChip id={g.id} level={g.level} />{/each}
    </div>
  {/if}
  <div class="ops">
    {#each l.members.slice(0, 14) as m (m.name)}<OpAvatar name={m.name} size={38} isKey={m.key} elite={m.elite} priority={pri(m)} />{/each}
    {#if l.members.length > 14}<span class="more">+{l.members.length - 14}</span>{/if}
    <!-- 「一组」干员（v1.0.1）：框在一起，左边写「任 N」 -->
    {#each l.groups ?? [] as g, gi (gi)}
      <span class="grp" title="这几个里任选 {g.need} 个"><em>任{g.need}</em>{#each g.members as m (m.name)}<OpAvatar name={m.name} size={38} isKey={m.key} elite={m.elite} priority={pri(m)} />{/each}</span>
    {/each}
  </div>
  {#if post.description}<p class="desc ugc">{post.description}</p>{/if}
  <div class="ft">
    {#if st}
      <span class="st"><img src={asset.strategy(st.id)} alt="" />{st.name}</span>
    {:else}
      <span class="st none">未选策略</span>
    {/if}
    <span class="num" title="点赞"><Icon name={post.liked ? 'heartFill' : 'heart'} size={14} />{post.likes}</span>
    <span class="num" title="导入次数"><Icon name="download" size={14} />{post.imports}</span>
    <span class="num" title="评论"><Icon name="chat" size={14} />{post.comments}</span>
  </div>
</a>

<style>
  .grp {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 2px 3px 2px 0;
    box-shadow: inset 0 0 0 1px rgb(var(--mint-rgb) / 0.35);
  }
  .grp em {
    padding: 0 4px;
    font-style: normal;
    font-size: 12px;
    color: var(--mint);
    white-space: nowrap;
  }
  .stg {
    flex: none;
    font-size: 11px;
    padding: 0 5px;
    color: var(--mint);
    box-shadow: inset 0 0 0 1px var(--mint);
  }
  .pc {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 14px 14px 12px;
    background:
      var(--hatch),
      linear-gradient(180deg, var(--bg-2), var(--bg-1));
    box-shadow: inset 0 0 0 1px var(--line-soft);
    transition:
      box-shadow var(--t-fast),
      transform var(--t-fast) var(--ease-out);
  }
  .pc::before {
    content: '';
    position: absolute;
    left: 0;
    top: 0;
    width: 40px;
    height: 2px;
    background: var(--mint);
    opacity: 0.6;
  }
  .pc:hover {
    box-shadow: inset 0 0 0 1px var(--mint-lo);
    transform: translateY(-1px);
  }
  .pc.featured::before {
    background: var(--gold);
    opacity: 1;
    width: 64px;
  }
  .pc.old {
    opacity: 0.7;
  }
  .hd {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .nm {
    font-size: 16px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .feat {
    flex: none;
    display: inline-flex;
    align-items: center;
    gap: 3px;
    padding: 1px 6px;
    font-size: 11px;
    font-weight: 700;
    color: var(--gold-ink);
    background: var(--gold);
  }
  .oldtag {
    flex: none;
    font-size: 11px;
    padding: 1px 6px;
    color: var(--fg-3);
    box-shadow: inset 0 0 0 1px var(--line);
  }
  .by {
    margin-top: -4px;
    font-size: 12px;
    color: var(--fg-3);
  }
  .who:hover {
    color: var(--mint);
    text-decoration: underline;
  }
  .goals {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }
  .ops {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .more {
    align-self: center;
    font-family: var(--font-term);
    color: var(--fg-3);
  }
  .desc {
    margin: 0;
    font-size: 13px;
    color: var(--fg-2);
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
    white-space: pre-line;
  }
  .ft {
    display: flex;
    align-items: center;
    gap: 14px;
    margin-top: auto;
    padding-top: 8px;
    border-top: 1px solid var(--line-soft);
    font-size: 12px;
    color: var(--fg-3);
  }
  .st {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    margin-right: auto;
    min-width: 0;
    color: var(--fg-2);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .st img {
    width: 20px;
    height: 20px;
    object-fit: cover;
  }
  .st.none {
    color: var(--fg-4);
  }
  .num {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    font-family: var(--font-term);
  }
</style>
