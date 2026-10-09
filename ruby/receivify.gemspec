# frozen_string_literal: true

require_relative "lib/receivify/version"

Gem::Specification.new do |spec|
  spec.name = "receivify"
  spec.version = Receivify::VERSION
  spec.authors = ["Receivify"]

  spec.summary = "Official Ruby SDK for the Receivify crypto payments API."
  spec.description = "Create crypto invoices and forwarding addresses, auto-paginate lists, and verify " \
                     "signed webhooks against the Receivify API. Standard library only, no runtime dependencies."
  spec.homepage = "https://receivify.io"
  spec.license = "MIT"
  spec.required_ruby_version = ">= 2.7.0"

  spec.metadata = {
    "homepage_uri" => "https://receivify.io",
    "documentation_uri" => "https://receivify.io/docs",
    "source_code_uri" => "https://github.com/receivify/receivify-sdks/tree/main/ruby",
    "bug_tracker_uri" => "https://github.com/receivify/receivify-sdks/issues",
    "rubygems_mfa_required" => "true"
  }

  spec.files = %w[
    LICENSE
    README.md
    receivify.gemspec
    lib/receivify.rb
    lib/receivify/version.rb
    lib/receivify/errors.rb
    lib/receivify/http.rb
    lib/receivify/resource.rb
    lib/receivify/resources.rb
    lib/receivify/page.rb
    lib/receivify/webhook.rb
    lib/receivify/money.rb
    lib/receivify/client.rb
  ]
  spec.require_paths = ["lib"]
end
