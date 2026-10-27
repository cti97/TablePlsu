-- Test fixture for integration tests. Run against an empty database "tableplsu_test".
create schema sales;
create table public.customers(id serial primary key, name text not null, email text unique, created_at timestamptz default now(), meta jsonb, active boolean default true);
create table sales.orders(id bigserial primary key, customer_id int references public.customers(id), total numeric(12,2), status text, placed_at date);
create table public.no_pk(a int, b text);
create view public.active_customers as select * from customers where active;
create materialized view sales.order_totals as select customer_id, sum(total) t from sales.orders group by 1;
create function public.add_one(x int) returns int language sql as $$ select x+1 $$;
insert into customers(name,email,meta) select 'Customer '||g, 'c'||g||'@x.com', jsonb_build_object('n',g) from generate_series(1,5000) g;
insert into sales.orders(customer_id,total,status,placed_at) select (g%5000)+1, (g%997)::numeric/3, (array['new','paid','shipped'])[g%3+1], date '2024-01-01' + (g%600) from generate_series(1,200000) g;
