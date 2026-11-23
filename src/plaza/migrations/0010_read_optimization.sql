ALTER TABLE posts ADD COLUMN search_text TEXT NOT NULL DEFAULT '';

/* 仅在写入时展开 JSON，以换行隔开字段，避免搜索词跨字段命中。 */
CREATE VIEW post_search_content AS
WITH covenant_names AS (SELECT key AS name,value AS id FROM json_each('{"乌萨斯":"ursus","伊比利亚":"iberia","卡西米尔":"kazimierz","叙拉古":"siracusa","炎":"yan","维多利亚":"victoria","莱塔尼亚":"lethania","萨尔贡":"sargon","谢拉格":"kjerag","阿戈尔":"egir","不屈":"indom","助力":"deput","协防干员":"empty","坚守":"stead","奇迹":"mira","奥术":"arcane","投资人":"invest","灵巧":"skillful","独行":"solo","突袭":"raid","精准":"preci","绝技":"sunt","聚合":"doc","调和":"mani","迅捷":"swift","远见":"visi"}'))
SELECT p.id,
 COALESCE(json_extract(p.lineup,'$.name'),'') || char(10) || p.description || char(10) ||
 COALESCE((SELECT group_concat(json_extract(m.value,'$.name'),char(10)) FROM
 (SELECT p.lineup AS value UNION ALL SELECT value FROM json_each(p.lineup,'$.stages')) stage,
 json_each(stage.value,'$.members') m),'') || char(10) ||
 COALESCE((SELECT group_concat(json_extract(e.value,'$.name'),char(10)) FROM
 (SELECT p.lineup AS value UNION ALL SELECT value FROM json_each(p.lineup,'$.stages')) stage,
 json_each(stage.value,'$.members') m, json_each(m.value,'$.equipment') e),'') || char(10) ||
 COALESCE((SELECT group_concat(e.value,char(10)) FROM
 (SELECT p.lineup AS value UNION ALL SELECT value FROM json_each(p.lineup,'$.stages')) stage,
 json_each(stage.value,'$.equipment') e),'') || char(10) ||
 COALESCE((SELECT group_concat(c.name,char(10)) FROM covenant_names c JOIN json_each(p.lineup,'$.goals') g
 ON c.id=json_extract(g.value,'$.id')),'') AS text FROM posts p;
CREATE TRIGGER post_search_insert AFTER INSERT ON posts BEGIN
 UPDATE posts SET search_text=(SELECT text FROM post_search_content WHERE id=NEW.id) WHERE id=NEW.id;
END;
CREATE TRIGGER post_search_update AFTER UPDATE OF lineup,description ON posts BEGIN
 UPDATE posts SET search_text=(SELECT text FROM post_search_content WHERE id=NEW.id) WHERE id=NEW.id;
END;
UPDATE posts SET search_text=(SELECT text FROM post_search_content WHERE id=posts.id);
CREATE INDEX codes_created_at ON login_codes(created_at);
/* 全赛季热门和精选排序索引。 */
CREATE INDEX posts_all_hot ON posts(status, (likes * 2 + imports) DESC, created_at DESC, id DESC);
CREATE INDEX posts_all_featured ON posts(status, featured, created_at DESC, id DESC);
CREATE TABLE stats_cache (id INTEGER PRIMARY KEY CHECK(id=1), data TEXT, updated_at INTEGER NOT NULL);
INSERT INTO stats_cache VALUES(1,NULL,0);
