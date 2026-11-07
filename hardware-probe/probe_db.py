import sqlite3, os, json

base = os.path.join(os.environ['TEMP'], 'uz801probe')

for name in ('mmssms.db', 'telephony.db', 'contacts2.db'):
    p = os.path.join(base, name)
    print('=' * 20, name)
    con = sqlite3.connect(p)
    tables = [r[0] for r in con.execute("select name from sqlite_master where type='table'")]
    print('tables:', tables)
    if name == 'mmssms.db':
        print('sms columns:', [d[1] for d in con.execute('pragma table_info(sms)')])
        print('sms count:', list(con.execute('select count(*) from sms')))
        print('threads count:', list(con.execute('select count(*) from threads')))
    if name == 'telephony.db':
        print('apn columns:', [d[1] for d in con.execute('pragma table_info(apn)')])
        for r in con.execute('select _id,name,apn,type,current,numeric,mcc,mnc from apn'):
            print('apn:', r)
    con.close()
