-- Set-based normalize of staged FARS years (stg_fars.*, all columns text) into event and vehicle.
-- Parameters: @years int[], @run_id uuid. Empty or blank staged fields count as missing.
-- Result rows (kind, name, n): kind 'rows' gives row counts per target, kind 'unmapped' gives
-- "{attribute}:{column}={value}" counts of vehicles whose attribute fell back to unknown.

drop table if exists pg_temp.fars_event, pg_temp.fars_vehicle, pg_temp.fars_person, pg_temp.fars_event_cas,
    pg_temp.fars_vehicle_cas, pg_temp.fars_vehicle_value, pg_temp.fars_vehicle_attr, pg_temp.fars_unmapped;

-- One row per crash.
create temp table fars_event on commit drop as
select a.data_year || '-' || trim(a.st_case) as source_key,
       a.data_year,
       trim(a.st_case) as st_case,
       make_date(nullif(trim(a.year), '')::int, nullif(trim(a.month), '')::int, nullif(trim(a.day), '')::int) as occurred_on,
       case when nullif(trim(a.hour), '')::int between 0 and 23 and nullif(trim(a.minute), '')::int between 0 and 59
            then make_time(trim(a.hour)::int, trim(a.minute)::int, 0) end as occurred_time,
       -- Unknown codes: latitude 77.7777 not reported, 88.8888 not available, 99.9999 unknown (inside +-90, so
       -- listed); longitude 777.7777, 888.8888, 999.9999 (outside +-180).
       case when abs(nullif(trim(a.latitude), '')::float8) <= 90
             and nullif(trim(a.latitude), '')::float8 not in (77.7777, 88.8888)
            then nullif(trim(a.latitude), '')::float8 end as latitude,
       case when abs(nullif(trim(a.longitud), '')::float8) <= 180 then nullif(trim(a.longitud), '')::float8 end as longitude,
       coalesce(nullif(trim(a.statename), ''), nullif(trim(a.state), '')) as state,
       to_jsonb(a) as payload
from stg_fars.accident a
where a.data_year::int = any(@years);
analyze fars_event;

-- One row per motor vehicle in transport (vehicle.csv; parked and working vehicles are in parkwork.csv).
create temp table fars_vehicle on commit drop as
select e.source_key,
       nullif(trim(v.veh_no), '')::int as veh_no,
       extract(year from e.occurred_on)::int as crash_year,
       nullif(trim(v.mod_year), '')::int as mod_year,
       nullif(trim(v.numoccs), '')::int as numoccs,
       to_jsonb(v) as payload
from stg_fars.vehicle v
join fars_event e on e.data_year = v.data_year and e.st_case = trim(v.st_case);
analyze fars_vehicle;

-- Counted casualties (inj_sev 4 fatal, 3 serious, 1 or 2 minor; 0, 5, 6 died prior to crash, 9 are not counted)
-- with the vehicle they are attributed to. role null means unattributed.
create temp table fars_person on commit drop as
select e.source_key,
       case x.inj_sev when 4 then 'fatal' when 3 then 'serious' else 'minor' end as sev,
       case when occ.veh_no is not null then 'occupant' when str.veh_no is not null then 'nonoccupant' end as role,
       coalesce(occ.veh_no, str.veh_no) as veh_no
from stg_fars.person p
join fars_event e on e.data_year = p.data_year and e.st_case = trim(p.st_case)
cross join lateral (select nullif(trim(p.veh_no), '')::int as veh_no,
                           nullif(trim(p.str_veh), '')::int as str_veh,
                           nullif(trim(p.inj_sev), '')::int as inj_sev) x
left join fars_vehicle occ on x.veh_no > 0 and occ.source_key = e.source_key and occ.veh_no = x.veh_no
left join fars_vehicle str on x.veh_no = 0 and str.source_key = e.source_key and str.veh_no = x.str_veh
where x.inj_sev in (1, 2, 3, 4);
analyze fars_person;

create temp table fars_event_cas on commit drop as
select source_key,
       count(*) filter (where sev = 'fatal') as fatalities,
       count(*) filter (where sev = 'serious') as serious,
       count(*) filter (where sev = 'minor') as minor,
       count(*) filter (where sev = 'fatal' and role is null) as u_fatalities,
       count(*) filter (where sev = 'serious' and role is null) as u_serious,
       count(*) filter (where sev = 'minor' and role is null) as u_minor
from fars_person
group by source_key;

create temp table fars_vehicle_cas on commit drop as
select source_key, veh_no,
       count(*) filter (where role = 'occupant' and sev = 'fatal') as o_fatalities,
       count(*) filter (where role = 'occupant' and sev = 'serious') as o_serious,
       count(*) filter (where role = 'occupant' and sev = 'minor') as o_minor,
       count(*) filter (where role = 'nonoccupant' and sev = 'fatal') as n_fatalities,
       count(*) filter (where role = 'nonoccupant' and sev = 'serious') as n_serious,
       count(*) filter (where role = 'nonoccupant' and sev = 'minor') as n_minor
from fars_person
where role is not null
group by source_key, veh_no;

-- The value of every vehicle column that vocabulary_map maps (any column of the staged vehicle row), extracted
-- once so the mapping below is a plain hash join.
create temp table fars_vehicle_value on commit drop as
select v.source_key, v.veh_no, c.source_column, trim(v.payload ->> c.source_column) as value
from fars_vehicle v
cross join (select distinct source_column from vocabulary_map where source_id = 'fars') c;
analyze fars_vehicle_value;

-- Vocabulary per vehicle and attribute: the matching vocabulary_map row with the lowest priority.
create temp table fars_vehicle_attr on commit drop as
select distinct on (x.source_key, x.veh_no, m.attribute) x.source_key, x.veh_no, m.attribute, m.code
from fars_vehicle_value x
join vocabulary_map m on m.source_id = 'fars' and m.source_column = x.source_column and m.source_value = x.value
order by x.source_key, x.veh_no, m.attribute, m.priority, m.source_column;
analyze fars_vehicle_attr;

-- A vehicle with no match for an attribute is reported under the last-resort column(s) of the attribute: those of its
-- highest-numbered priority (body_typ for vehicle_class, spec_use for regulation_class).
create temp table fars_unmapped on commit drop as
with fallback as (
    select m.attribute, m.source_column
    from vocabulary_map m
    where m.source_id = 'fars'
      and m.priority = (select max(x.priority) from vocabulary_map x where x.source_id = 'fars' and x.attribute = m.attribute)
    group by m.attribute, m.source_column)
select f.attribute || ':' || f.source_column || '=' || coalesce(x.value, '') as name, count(*) as n
from fars_vehicle_value x
join fallback f on f.source_column = x.source_column
where not exists (select 1 from fars_vehicle_attr a
                  where a.source_key = x.source_key and a.veh_no = x.veh_no and a.attribute = f.attribute)
group by 1;

-- Re-ingest replaces the vehicles of every crash it touches.
delete from vehicle v
using event e
where v.event_id = e.event_id
  and e.source_id = 'fars'
  and e.source_key in (select source_key from fars_event);

-- R25: a crash NHTSA dropped from a re-released year goes too (its vehicles by cascade). Only the run's years:
-- the source key starts with the data year.
delete from event e
where e.source_id = 'fars'
  and split_part(e.source_key, '-', 1)::int = any(@years)
  and not exists (select 1 from fars_event f where f.source_key = e.source_key);

insert into event (event_id, source_id, source_key, mode, event_type, occurred_on, occurred_time, latitude, longitude,
                   state, country, severity, fatalities, serious_injuries, minor_injuries, unattributed_fatalities,
                   unattributed_serious_injuries, unattributed_minor_injuries, source_payload, ingest_run_id)
select gen_random_uuid(), 'fars', e.source_key, 'highway', 'accident', e.occurred_on, e.occurred_time, e.latitude,
       e.longitude, e.state, 'US', 'fatal', coalesce(c.fatalities, 0), coalesce(c.serious, 0), coalesce(c.minor, 0),
       coalesce(c.u_fatalities, 0), coalesce(c.u_serious, 0), coalesce(c.u_minor, 0), e.payload, @run_id
from fars_event e
left join fars_event_cas c on c.source_key = e.source_key
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
    unattributed_fatalities = excluded.unattributed_fatalities,
    unattributed_serious_injuries = excluded.unattributed_serious_injuries,
    unattributed_minor_injuries = excluded.unattributed_minor_injuries,
    source_payload = excluded.source_payload,
    ingest_run_id = excluded.ingest_run_id;

insert into vehicle (vehicle_id, event_id, vehicle_no, make, model, year_built, vehicle_class, operator_type,
                     regulation_class, maintenance_regime, occupants, crew_count, vehicle_age_years,
                     occupant_fatalities, occupant_serious_injuries, occupant_minor_injuries, nonoccupant_fatalities,
                     nonoccupant_serious_injuries, nonoccupant_minor_injuries, source_payload, ingest_run_id)
select gen_random_uuid(), ev.event_id, v.veh_no,
       coalesce(nullif(trim(v.payload ->> 'makename'), ''), nullif(trim(v.payload ->> 'make'), '')),
       coalesce(nullif(trim(v.payload ->> 'mak_modname'), ''), nullif(trim(v.payload ->> 'model'), '')),
       y.year_built,
       coalesce(vc.code, 'unknown'), coalesce(ot.code, 'unknown'), coalesce(rc.code, 'unknown'),
       coalesce(mr.code, 'unknown'),
       case when v.numoccs < 97 then v.numoccs end,
       1,
       case when v.crash_year - y.year_built >= 0 then v.crash_year - y.year_built end,
       coalesce(c.o_fatalities, 0), coalesce(c.o_serious, 0), coalesce(c.o_minor, 0),
       coalesce(c.n_fatalities, 0), coalesce(c.n_serious, 0), coalesce(c.n_minor, 0),
       v.payload, @run_id
from fars_vehicle v
join event ev on ev.source_id = 'fars' and ev.source_key = v.source_key
cross join lateral (select case when v.mod_year not in (9998, 9999) then v.mod_year end as year_built) y
left join fars_vehicle_attr vc on vc.source_key = v.source_key and vc.veh_no = v.veh_no and vc.attribute = 'vehicle_class'
left join fars_vehicle_attr ot on ot.source_key = v.source_key and ot.veh_no = v.veh_no and ot.attribute = 'operator_type'
left join fars_vehicle_attr rc on rc.source_key = v.source_key and rc.veh_no = v.veh_no and rc.attribute = 'regulation_class'
left join fars_vehicle_attr mr on mr.source_key = v.source_key and mr.veh_no = v.veh_no and mr.attribute = 'maintenance_regime'
left join fars_vehicle_cas c on c.source_key = v.source_key and c.veh_no = v.veh_no;

select 'rows' as kind, 'event' as name, count(*)::bigint as n from fars_event
union all
select 'rows', 'vehicle', count(*)::bigint from fars_vehicle
union all
select 'unmapped', name, n from fars_unmapped;
