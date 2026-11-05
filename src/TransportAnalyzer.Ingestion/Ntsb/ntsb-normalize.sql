-- Set-based normalize of a staged NTSB snapshot (stg_ntsb.*, all columns text) into event and vehicle (one per aircraft).
-- Parameters: @years int[] (null = every year present in the snapshot), @snapshot text, @run_id uuid.
-- Empty or blank staged fields count as missing.
-- Result rows (kind, name, n): kind 'rows' gives row counts per target, kind 'unmapped' gives
-- "{attribute}:{column}={value}" counts of aircraft whose attribute fell back to unknown.

drop table if exists pg_temp.ntsb_years, pg_temp.ntsb_event, pg_temp.ntsb_engine, pg_temp.ntsb_aircraft,
    pg_temp.ntsb_event_aircraft, pg_temp.ntsb_injury, pg_temp.ntsb_aircraft_cas, pg_temp.ntsb_orphan_cas, pg_temp.ntsb_narrative,
    pg_temp.ntsb_aircraft_value, pg_temp.ntsb_aircraft_attr, pg_temp.ntsb_unmapped;

create temp table ntsb_years on commit drop as
select y from unnest(@years) y where y is not null
union
select nullif(trim(e.ev_year), '')::int from stg_ntsb.events e where e.snapshot = @snapshot and @years is null;

-- One row per event of the run's years. ev_date is "MM/DD/YY HH:MM:SS" with a two-digit year, so the year and month
-- come from ev_year/ev_month and only the day from ev_date.
create temp table ntsb_event on commit drop as
select trim(e.ev_id) as source_key,
       nullif(trim(e.ev_year), '')::int as ev_year,
       case trim(e.ev_type) when 'ACC' then 'accident' when 'INC' then 'incident' else 'unknown' end as event_type,
       make_date(nullif(trim(e.ev_year), '')::int, nullif(trim(e.ev_month), '')::int,
                 nullif(split_part(trim(e.ev_date), '/', 2), '')::int) as occurred_on,
       case when abs(n.lat) <= 90 then n.lat end as latitude,
       case when abs(n.lon) <= 180 then n.lon end as longitude,
       nullif(trim(e.ev_state), '') as state,
       case when trim(e.ev_country) = 'USA' then 'US' else nullif(trim(e.ev_country), '') end as country,
       case trim(e.ev_highest_injury) when 'FATL' then 'fatal' when 'SERS' then 'serious' when 'MINR' then 'minor'
            when 'NONE' then 'none' else 'unknown' end as severity,
       -- Blank ground counts are taken as zero.
       coalesce(nullif(trim(e.inj_f_grnd), '')::int, 0) as ground_f,
       coalesce(nullif(trim(e.inj_s_grnd), '')::int, 0) as ground_s,
       coalesce(nullif(trim(e.inj_m_grnd), '')::int, 0) as ground_m,
       to_jsonb(e) as payload
from stg_ntsb.events e
cross join lateral (
    select case when trim(e.dec_latitude) ~ '^[-+]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                then trim(e.dec_latitude)::float8 end as lat,
           case when trim(e.dec_longitude) ~ '^[-+]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                then trim(e.dec_longitude)::float8 end as lon) n
where e.snapshot = @snapshot
  and nullif(trim(e.ev_year), '')::int in (select y from ntsb_years);
analyze ntsb_event;

-- Engine type of each aircraft's lowest-numbered engine.
create temp table ntsb_engine on commit drop as
select distinct on (ev_id, aircraft_key) ev_id, aircraft_key, eng_type
from (select trim(g.ev_id) as ev_id, nullif(trim(g.aircraft_key), '')::int as aircraft_key,
             coalesce(trim(g.eng_type), '') as eng_type, nullif(trim(g.eng_no), '')::int as eng_no
      from stg_ntsb.engines g
      where g.snapshot = @snapshot) g
order by ev_id, aircraft_key, eng_no nulls last, eng_type;

-- One row per aircraft, with the derived columns vocabulary_map matches on (trimmed parts, blank = empty string):
-- homebuilt, vehicle_profile = acft_category:eng_type:far_part, reg_profile = far_part:oper_sched,
-- use_profile = far_part:type_fly.
create temp table ntsb_aircraft on commit drop as
select e.source_key,
       nullif(trim(a.aircraft_key), '')::int as aircraft_key,
       e.ev_year,
       nullif(trim(a.regis_no), '') as registration,
       nullif(trim(a.acft_make), '') as make,
       nullif(trim(a.acft_model), '') as model,
       y.year_built,
       nullif(trim(a.damage), '') as damage,
       coalesce(trim(a.homebuilt), '') as homebuilt,
       coalesce(trim(a.acft_category), '') || ':' || coalesce(g.eng_type, '') || ':' || coalesce(trim(a.far_part), '')
           as vehicle_profile,
       coalesce(trim(a.far_part), '') || ':' || coalesce(trim(a.oper_sched), '') as reg_profile,
       coalesce(trim(a.far_part), '') || ':' || coalesce(trim(a.type_fly), '') as use_profile,
       to_jsonb(a) as payload
from stg_ntsb.aircraft a
join ntsb_event e on e.source_key = trim(a.ev_id)
left join ntsb_engine g on g.ev_id = trim(a.ev_id) and g.aircraft_key = nullif(trim(a.aircraft_key), '')::int
-- acft_year holds junk such as 0 or 164: only years from 1900 to the event year count.
cross join lateral (select case when trim(a.acft_year) ~ '^[0-9]{1,4}$' and trim(a.acft_year)::int between 1900 and e.ev_year
                                then trim(a.acft_year)::int end as year_built) y
where a.snapshot = @snapshot;
analyze ntsb_aircraft;

create temp table ntsb_event_aircraft on commit drop as
select source_key, count(*) as aircraft, min(aircraft_key) as first_key
from ntsb_aircraft
group by source_key;

-- Injury rows per person category and level; TOTL rows are unreliable (often blank or inconsistent) and ignored.
create temp table ntsb_injury on commit drop as
select e.source_key,
       nullif(trim(i.aircraft_key), '')::int as aircraft_key,
       trim(i.inj_person_category) as category,
       trim(i.injury_level) as level,
       nullif(trim(i.inj_person_count), '')::int as n
from stg_ntsb.injury i
join ntsb_event e on e.source_key = trim(i.ev_id)
where i.snapshot = @snapshot
  and trim(i.injury_level) in ('FATL', 'SERS', 'MINR', 'NONE');

-- Occupants per aircraft. occupants and crew_count stay null when there is nothing to sum.
create temp table ntsb_aircraft_cas on commit drop as
select i.source_key, i.aircraft_key,
       coalesce(sum(i.n) filter (where i.level = 'FATL'), 0) as o_fatalities,
       coalesce(sum(i.n) filter (where i.level = 'SERS'), 0) as o_serious,
       coalesce(sum(i.n) filter (where i.level = 'MINR'), 0) as o_minor,
       sum(i.n) as occupants,
       sum(i.n) filter (where i.category in ('Flig', 'Cabi')) as crew
from ntsb_injury i
where exists (select 1 from ntsb_aircraft a where a.source_key = i.source_key and a.aircraft_key = i.aircraft_key)
group by i.source_key, i.aircraft_key;

-- Injury rows of an Aircraft_Key with no aircraft row cannot be attributed; they stay on the event.
create temp table ntsb_orphan_cas on commit drop as
select i.source_key,
       coalesce(sum(i.n) filter (where i.level = 'FATL'), 0) as f,
       coalesce(sum(i.n) filter (where i.level = 'SERS'), 0) as s,
       coalesce(sum(i.n) filter (where i.level = 'MINR'), 0) as m
from ntsb_injury i
where not exists (select 1 from ntsb_aircraft a where a.source_key = i.source_key and a.aircraft_key = i.aircraft_key)
group by i.source_key;

-- Narrative and probable cause from the lowest Aircraft_Key narrative row: factual text unless blank or the
-- placeholder "import", then the preliminary text under the same rule.
create temp table ntsb_narrative on commit drop as
select distinct on (r.source_key) r.source_key,
       case when coalesce(trim(r.accf), '') not in ('', 'import') then trim(r.accf)
            when coalesce(trim(r.accp), '') not in ('', 'import') then trim(r.accp) end as narrative,
       nullif(trim(r.cause), '') as probable_cause
from (select e.source_key, nullif(trim(n.aircraft_key), '')::int as aircraft_key,
             n.narr_accf as accf, n.narr_accp as accp, n.narr_cause as cause
      from stg_ntsb.narratives n
      join ntsb_event e on e.source_key = trim(n.ev_id)
      where n.snapshot = @snapshot) r
order by r.source_key, r.aircraft_key nulls last;

create temp table ntsb_aircraft_value on commit drop as
select a.source_key, a.aircraft_key, v.source_column, v.value
from ntsb_aircraft a
cross join lateral (values ('homebuilt', a.homebuilt), ('vehicle_profile', a.vehicle_profile),
                           ('reg_profile', a.reg_profile), ('use_profile', a.use_profile)) v(source_column, value);
analyze ntsb_aircraft_value;

-- Vocabulary per aircraft and attribute: source_value is a LIKE pattern (% any run; a literal underscore is written
-- \_ in ntsb.csv), the matching row with the lowest priority wins.
create temp table ntsb_aircraft_attr on commit drop as
select distinct on (x.source_key, x.aircraft_key, m.attribute) x.source_key, x.aircraft_key, m.attribute, m.code
from ntsb_aircraft_value x
join vocabulary_map m on m.source_id = 'ntsb' and m.source_column = x.source_column
                     and x.value like m.source_value escape '\'
order by x.source_key, x.aircraft_key, m.attribute, m.priority, m.source_column, m.source_value;
analyze ntsb_aircraft_attr;

-- An aircraft with no match for an attribute is reported under the attribute's last-resort column: the column of its
-- highest-numbered priority (vehicle_profile for vehicle_class, reg_profile for regulation_class and
-- maintenance_regime, use_profile for operator_type).
create temp table ntsb_unmapped on commit drop as
with fallback as (
    select m.attribute, m.source_column
    from vocabulary_map m
    where m.source_id = 'ntsb'
      and m.priority = (select max(x.priority) from vocabulary_map x where x.source_id = 'ntsb' and x.attribute = m.attribute)
    group by m.attribute, m.source_column)
select f.attribute || ':' || f.source_column || '=' || x.value as name, count(*) as n
from ntsb_aircraft_value x
join fallback f on f.source_column = x.source_column
where not exists (select 1 from ntsb_aircraft_attr a
                  where a.source_key = x.source_key and a.aircraft_key = x.aircraft_key and a.attribute = f.attribute)
group by 1;

-- Re-ingest replaces the aircraft of every event it touches.
delete from vehicle v
using event e
where v.event_id = e.event_id
  and e.source_id = 'ntsb'
  and e.source_key in (select source_key from ntsb_event);

-- An event no longer in the snapshot goes too (its vehicles by cascade), for the run's years only.
delete from event e
where e.source_id = 'ntsb'
  and extract(year from e.occurred_on)::int in (select y from ntsb_years)
  and not exists (select 1 from ntsb_event f where f.source_key = e.source_key);

-- Ground casualties belong to the aircraft when there is exactly one, otherwise to the event. operation_class is the
-- regulation class of the lowest-numbered aircraft.
insert into event (event_id, source_id, source_key, mode, event_type, occurred_on, occurred_time, latitude, longitude,
                   state, country, severity, fatalities, serious_injuries, minor_injuries, ground_fatalities,
                   unattributed_fatalities, unattributed_serious_injuries, unattributed_minor_injuries, operation_class,
                   narrative, probable_cause, source_payload, ingest_run_id)
select gen_random_uuid(), 'ntsb', e.source_key, 'aviation', e.event_type, e.occurred_on, null, e.latitude, e.longitude,
       e.state, e.country, e.severity,
       coalesce(c.f, 0) + coalesce(o.f, 0) + e.ground_f,
       coalesce(c.s, 0) + coalesce(o.s, 0) + e.ground_s,
       coalesce(c.m, 0) + coalesce(o.m, 0) + e.ground_m,
       e.ground_f,
       coalesce(o.f, 0) + case when k.aircraft = 1 then 0 else e.ground_f end,
       coalesce(o.s, 0) + case when k.aircraft = 1 then 0 else e.ground_s end,
       coalesce(o.m, 0) + case when k.aircraft = 1 then 0 else e.ground_m end,
       case when k.source_key is not null then coalesce(rc.code, 'unknown') end, n.narrative, n.probable_cause, e.payload, @run_id
from ntsb_event e
left join ntsb_event_aircraft k on k.source_key = e.source_key
left join (select source_key, sum(o_fatalities) f, sum(o_serious) s, sum(o_minor) m
           from ntsb_aircraft_cas group by source_key) c on c.source_key = e.source_key
left join ntsb_orphan_cas o on o.source_key = e.source_key
left join ntsb_narrative n on n.source_key = e.source_key
left join ntsb_aircraft_attr rc on rc.source_key = k.source_key and rc.aircraft_key = k.first_key
                               and rc.attribute = 'regulation_class'
on conflict (source_id, source_key) do update set
    mode = excluded.mode,
    event_type = excluded.event_type,
    occurred_on = excluded.occurred_on,
    occurred_time = excluded.occurred_time,
    latitude = excluded.latitude,
    longitude = excluded.longitude,
    state = excluded.state,
    country = excluded.country,
    severity = excluded.severity,
    fatalities = excluded.fatalities,
    serious_injuries = excluded.serious_injuries,
    minor_injuries = excluded.minor_injuries,
    ground_fatalities = excluded.ground_fatalities,
    unattributed_fatalities = excluded.unattributed_fatalities,
    unattributed_serious_injuries = excluded.unattributed_serious_injuries,
    unattributed_minor_injuries = excluded.unattributed_minor_injuries,
    operation_class = excluded.operation_class,
    narrative = excluded.narrative,
    probable_cause = excluded.probable_cause,
    source_payload = excluded.source_payload,
    ingest_run_id = excluded.ingest_run_id;

insert into vehicle (vehicle_id, event_id, vehicle_no, registration, make, model, year_built, damage, vehicle_class,
                     operator_type, regulation_class, maintenance_regime, occupants, crew_count, vehicle_age_years,
                     occupant_fatalities, occupant_serious_injuries, occupant_minor_injuries, nonoccupant_fatalities,
                     nonoccupant_serious_injuries, nonoccupant_minor_injuries, source_payload, ingest_run_id)
select gen_random_uuid(), ev.event_id, a.aircraft_key, a.registration, a.make, a.model, a.year_built, a.damage,
       coalesce(vc.code, 'unknown'), coalesce(ot.code, 'unknown'), coalesce(rc.code, 'unknown'),
       coalesce(mr.code, 'unknown'),
       c.occupants, c.crew,
       a.ev_year - a.year_built,
       coalesce(c.o_fatalities, 0), coalesce(c.o_serious, 0), coalesce(c.o_minor, 0),
       case when k.aircraft = 1 then e.ground_f else 0 end,
       case when k.aircraft = 1 then e.ground_s else 0 end,
       case when k.aircraft = 1 then e.ground_m else 0 end,
       a.payload, @run_id
from ntsb_aircraft a
join ntsb_event e on e.source_key = a.source_key
join event ev on ev.source_id = 'ntsb' and ev.source_key = a.source_key
join ntsb_event_aircraft k on k.source_key = a.source_key
left join ntsb_aircraft_cas c on c.source_key = a.source_key and c.aircraft_key = a.aircraft_key
left join ntsb_aircraft_attr vc on vc.source_key = a.source_key and vc.aircraft_key = a.aircraft_key and vc.attribute = 'vehicle_class'
left join ntsb_aircraft_attr ot on ot.source_key = a.source_key and ot.aircraft_key = a.aircraft_key and ot.attribute = 'operator_type'
left join ntsb_aircraft_attr rc on rc.source_key = a.source_key and rc.aircraft_key = a.aircraft_key and rc.attribute = 'regulation_class'
left join ntsb_aircraft_attr mr on mr.source_key = a.source_key and mr.aircraft_key = a.aircraft_key and mr.attribute = 'maintenance_regime';

select 'rows' as kind, 'event' as name, count(*)::bigint as n from ntsb_event
union all
select 'rows', 'vehicle', count(*)::bigint from ntsb_aircraft
union all
select 'unmapped', name, n from ntsb_unmapped;
