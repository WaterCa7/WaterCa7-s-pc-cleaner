<?php
// [ключ текста, free|pro, есть ли в программе сейчас]. Новые функции включаются true по мере выхода.
return [
    'home' => [
        ['f.home.score', 'free', true],
        ['f.home.quick', 'free', true],
        ['f.home.undo', 'free', true],
        ['f.home.full', 'pro', true],
        ['f.home.schedule', 'pro', false],
    ],
    'clean' => [
        ['f.clean.basic', 'free', true],
        ['f.clean.deep', 'pro', true],
        ['f.clean.dism', 'pro', true],
        ['f.clean.schedule', 'pro', false],
    ],
    'computer' => [
        ['f.comp.specs', 'free', true],
        ['f.comp.speed', 'pro', true],
        ['f.comp.history', 'pro', false],
    ],
    'system' => [
        ['f.sys.proc', 'free', true],
        ['f.sys.proc_deep', 'pro', true],
        ['f.sys.startup', 'free', true],
        ['f.sys.startup_deep', 'pro', true],
        ['f.sys.programs', 'free', true],
        ['f.sys.bloat', 'pro', true],
        ['f.sys.updates', 'pro', false],
        ['f.sys.disks', 'free', true],
        ['f.sys.disk_deep', 'pro', true],
        ['f.sys.dupes', 'pro', false],
    ],
    'protect' => [
        ['f.prot.basic', 'free', true],
        ['f.prot.deep', 'pro', true],
        ['f.prot.net', 'pro', true],
        ['f.prot.files', 'free', false],
        ['f.prot.wifi', 'pro', false],
        ['f.prot.downloads', 'pro', false],
        ['f.prot.leaks', 'pro', false],
    ],
    'phone' => [
        ['f.phone.view', 'free', false],
        ['f.phone.backup', 'pro', false],
    ],
];
