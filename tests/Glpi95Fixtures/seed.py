"""Writes fictitious tickets, follow-ups, tasks and solutions as SQL for a throwaway GLPI 9.5 database.

Usage: seed.py [count=30] [first id=1]
"""
import sys
count = int(sys.argv[1]) if len(sys.argv) > 1 else 30
first = int(sys.argv[2]) if len(sys.argv) > 2 else 1
lines = []
for i in range(first, first + count):
    status = [1, 2, 3, 4, 5, 6][i % 6]
    category = 1 if i % 2 else 2
    title = f"Ticket fictif {i:03d} · {'Imprimante' if category == 1 else 'Connexion'} site exemple {'Nord' if i % 3 else 'Sud'}"
    day = 1 + (i % 27)
    lines.append(
        "INSERT INTO glpi_tickets (id, entities_id, name, date, date_mod, date_creation, users_id_lastupdater, status, users_id_recipient, "
        "requesttypes_id, content, urgency, impact, priority, itilcategories_id, type, global_validation) VALUES "
        f"({i}, 0, '{title}', '2026-09-{day:02d} 09:{i % 60:02d}:00', '2026-10-{day:02d} {10 + (i // 60) % 10:02d}:{i % 60:02d}:00', '2026-09-{day:02d} 09:{i % 60:02d}:00', 2, {status}, 2, 1, "
        f"'&lt;p&gt;Description fictive du ticket {i}.&lt;/p&gt;', 3, 3, 3, {category}, {1 if i % 4 else 2}, 1);")
    lines.append("INSERT INTO glpi_tickets_users (tickets_id, users_id, type, use_notification) VALUES "
                 f"({i}, 2, 1, 0), ({i}, 2, 2, 0);")
    for f in range(1, 1 + i % 3):
        lines.append("INSERT INTO glpi_itilfollowups (itemtype, items_id, date, users_id, content, is_private, requesttypes_id, date_mod, date_creation) VALUES "
                     f"('Ticket', {i}, '2026-10-{day:02d} 11:0{f}:00', 2, '&lt;p&gt;Suivi fictif {f} du ticket {i}.&lt;/p&gt;', 0, 1, NOW(), NOW());")
    if i % 2 == 0:
        lines.append("INSERT INTO glpi_tickettasks (tickets_id, date, users_id, users_id_tech, content, is_private, actiontime, state, date_mod, date_creation) VALUES "
                     f"({i}, '2026-10-{day:02d} 12:00:00', 2, 2, '&lt;p&gt;Tâche fictive du ticket {i}.&lt;/p&gt;', 0, 1800, 2, NOW(), NOW());")
    if status in (5, 6):
        lines.append("INSERT INTO glpi_itilsolutions (itemtype, items_id, solutiontypes_id, content, date_creation, date_mod, users_id, status) VALUES "
                     f"('Ticket', {i}, 0, '&lt;p&gt;Solution fictive : redémarrer le service du ticket {i}.&lt;/p&gt;', NOW(), NOW(), 2, 3);")
print("\n".join(lines))
