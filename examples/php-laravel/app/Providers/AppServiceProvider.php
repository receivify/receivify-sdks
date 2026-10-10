<?php

namespace App\Providers;

use Illuminate\Support\ServiceProvider;
use Receivify;

class AppServiceProvider extends ServiceProvider
{
    public function register(): void
    {
        // One Receivify client for the whole app, injected wherever a controller asks for it.
        $this->app->singleton(Receivify::class, function () {
            $config = config('services.receivify');
            if (empty($config['key']) || empty($config['webhook_secret'])) {
                // Put them in .env. (`php artisan serve` does not pass shell environment
                // variables through to the app, so .env is the reliable place in development.)
                throw new \RuntimeException('Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET in .env and add the receivify entry to config/services.php.');
            }

            return new Receivify($config['url'], $config['key'], [
                // A LIST: during a rotation, the new secret first and the old one second
                // ("whsec_new,whsec_old") so no delivery is rejected mid-switch.
                'webhook_secrets' => array_values(array_filter(array_map('trim', explode(',', (string) $config['webhook_secret'])))),
            ]);
        });
    }

    public function boot(): void
    {
    }
}
