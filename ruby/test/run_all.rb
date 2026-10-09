# frozen_string_literal: true

# Runs the whole suite without Rake:  ruby -Ilib -Itest test/run_all.rb
Dir[File.join(__dir__, "*_test.rb")].sort.each { |file| require file }
