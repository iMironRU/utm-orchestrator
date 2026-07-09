package ru.imironru.readerbinder;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * Загружает целевую привязку FSRAR-ID -> (индекс ридера, служба, порт)
 * из простого properties-подобного файла (см. bindings.properties).
 * Строки-комментарии (#) и с "?" вместо FSRAR-ID игнорируются.
 */
public class BindingConfig {

    public static class Entry {
        public final String fsrarId;
        public final int index;
        public final String serviceName;
        public final int port;

        Entry(String fsrarId, int index, String serviceName, int port) {
            this.fsrarId = fsrarId;
            this.index = index;
            this.serviceName = serviceName;
            this.port = port;
        }

        public String readerName() {
            return "Aktiv Rutoken ECP " + index;
        }
    }

    public static List<Entry> load(Path file) throws IOException {
        List<Entry> entries = new ArrayList<>();
        for (String line : Files.readAllLines(file)) {
            String trimmed = line.trim();
            if (trimmed.isEmpty() || trimmed.startsWith("#")) continue;

            int eq = trimmed.indexOf('=');
            if (eq < 0) continue;

            String fsrarId = trimmed.substring(0, eq).trim();
            if (!fsrarId.matches("\\d{10,14}")) continue; // пропускаем "????????????=..."

            String[] rest = trimmed.substring(eq + 1).trim().split(",");
            if (rest.length != 3) continue;

            entries.add(new Entry(fsrarId, Integer.parseInt(rest[0].trim()),
                    rest[1].trim(), Integer.parseInt(rest[2].trim())));
        }
        // Сортируем по убыванию индекса — именно в этом порядке 2UTM вводит
        // ридеры и запускает службы (см. NOTES.md/installUTM() в 2UTM_vs.cpp):
        // каждый новый (более низкий) индекс должен появляться в таблице
        // ридеров только после того, как предыдущая (более высокий индекс)
        // служба уже захватила свой слот 0.
        entries.sort((a, b) -> Integer.compare(b.index, a.index));
        return entries;
    }
}
