var count = 0;
StorageChecks.Run((ok, name) => {
    if (!ok) throw new Exception(name);
    count++; Console.WriteLine("PASS " + name);
});
Console.WriteLine($"{count} storage checks passed.");
